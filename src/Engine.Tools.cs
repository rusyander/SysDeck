// Windows Process Cleaner — «Инструменты»: быстрые исправления, защита, запуск штатных средств Windows
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace WindowsProcessCleaner
{
    // Пункт «Инструментов». Группа fix/protect выполняется с журналом; open — просто запускает программу.
    public class ToolItem
    {
        public string Id, Group, Title, Desc;
        public bool Confirm;             // меняет состояние системы — спросить перед запуском
        public string ConfirmText;
        public bool Admin;               // нужны права администратора
        public bool Long;                // минуты работы: показывать «Остановить»
        public string Open, Args;        // для группы open (и «открывающих» fix): что запустить
    }

    public partial class Engine
    {
        public const string ToolsFix = "fix", ToolsProtect = "protect", ToolsOpen = "open";

        private static ToolItem Tool(string id, string group, string title, string desc)
        {
            ToolItem t = new ToolItem();
            t.Id = id; t.Group = group; t.Title = title; t.Desc = desc;
            return t;
        }

        private static ToolItem Opener(string id, string title, string desc, string open, string args)
        {
            ToolItem t = Tool(id, ToolsOpen, title, desc);
            t.Open = open; t.Args = args;
            return t;
        }

        public static List<ToolItem> ToolCatalog()
        {
            List<ToolItem> l = new List<ToolItem>();
            ToolItem t;

            // ---- быстрые исправления ----
            l.Add(Tool("flushdns", ToolsFix, Tr.S("Сбросить кэш DNS", "Flush DNS cache"),
                Tr.S("ipconfig /flushdns и /registerdns: сайт «не открывается» после смены DNS или переезда сервера — первое, что помогает. Безопасно.",
                     "ipconfig /flushdns and /registerdns: a site that “won't open” after a DNS change or server move — the first thing that helps. Safe.")));
            t = Tool("netreset", ToolsFix, Tr.S("Сбросить сеть (Winsock, TCP/IP)", "Reset network (Winsock, TCP/IP)"),
                Tr.S("netsh winsock reset + netsh int ip reset: лечит «без доступа к интернету» после VPN, антивирусов и прокси. Нужна перезагрузка.",
                     "netsh winsock reset + netsh int ip reset: fixes “no internet access” left by VPNs, antiviruses and proxies. Needs a reboot."));
            t.Confirm = true; t.Admin = true;
            t.ConfirmText = Tr.S("Сбросить Winsock и настройки TCP/IP? Статические IP/DNS адаптеров вернутся к «автоматически», VPN-клиенты могут потребовать переустановки. После сброса нужна перезагрузка.",
                                 "Reset Winsock and TCP/IP settings? Static IP/DNS on adapters go back to “automatic”, VPN clients may need reinstalling. A reboot is required afterwards.");
            l.Add(t);
            t = Tool("explorer", ToolsFix, Tr.S("Перезапустить Проводник", "Restart Explorer"),
                Tr.S("Завершает explorer.exe и запускает заново: чинит зависшую панель задач, пропавшие иконки в трее, «висящий» рабочий стол. Открытые окна Проводника закроются.",
                     "Kills explorer.exe and starts it again: fixes a frozen taskbar, missing tray icons, a stuck desktop. Open Explorer windows will close."));
            t.Confirm = true;
            t.ConfirmText = Tr.S("Перезапустить Проводник? Все окна Проводника закроются, панель задач на пару секунд исчезнет.",
                                 "Restart Explorer? All Explorer windows will close and the taskbar disappears for a couple of seconds.");
            l.Add(t);
            t = Tool("iconcache", ToolsFix, Tr.S("Пересобрать кэш значков", "Rebuild icon cache"),
                Tr.S("Удаляет iconcache_*.db и перезапускает Проводник: чинит пустые или чужие значки у ярлыков и файлов. Windows соберёт кэш заново.",
                     "Deletes iconcache_*.db and restarts Explorer: fixes blank or wrong icons on shortcuts and files. Windows rebuilds the cache."));
            t.Confirm = true;
            t.ConfirmText = Tr.S("Удалить кэш значков и перезапустить Проводник? Окна Проводника закроются, значки на пару секунд станут пустыми.",
                                 "Delete the icon cache and restart Explorer? Explorer windows close, icons go blank for a couple of seconds.");
            l.Add(t);
            t = Tool("fontcache", ToolsFix, Tr.S("Пересобрать кэш шрифтов", "Rebuild font cache"),
                Tr.S("Останавливает службу FontCache, удаляет её файлы и запускает снова: чинит «квадратики» и кривые шрифты в программах.",
                     "Stops the FontCache service, deletes its files and starts it again: fixes “boxes” and garbled fonts in programs."));
            t.Confirm = true; t.Admin = true;
            t.ConfirmText = Tr.S("Пересобрать кэш шрифтов? Служба шрифтов будет перезапущена, первый запуск программ после этого чуть медленнее.",
                                 "Rebuild the font cache? The font service restarts; the first program launches afterwards are slightly slower.");
            t.Long = true;   // остановка службы + удаление кэша: до минуты, «Остановить» должна работать
            l.Add(t);
            t = Tool("sfc", ToolsFix, Tr.S("Проверить системные файлы (SFC)", "Check system files (SFC)"),
                Tr.S("sfc /scannow: сверяет файлы Windows с эталоном и восстанавливает повреждённые. 5–15 минут, ничего пользовательского не трогает.",
                     "sfc /scannow: compares Windows files with the reference copy and repairs damaged ones. 5–15 minutes, touches nothing of yours."));
            t.Admin = true; t.Long = true;
            l.Add(t);
            t = Tool("dism", ToolsFix, Tr.S("Восстановить образ Windows (DISM)", "Repair Windows image (DISM)"),
                Tr.S("DISM /RestoreHealth: чинит само хранилище компонентов, из которого SFC берёт эталон. Запускайте, если SFC «не смог восстановить». 5–20 минут, нужен интернет.",
                     "DISM /RestoreHealth: repairs the component store SFC takes its reference from. Run it when SFC “could not repair”. 5–20 minutes, needs internet."));
            t.Admin = true; t.Long = true;
            l.Add(t);
            t = Tool("chkdsk", ToolsFix, Tr.S("Проверить системный диск (chkdsk)", "Check the system drive (chkdsk)"),
                Tr.S("chkdsk /scan: онлайн-проверка файловой системы без перезагрузки; найденные ошибки помечает для исправления. Несколько минут.",
                     "chkdsk /scan: an online file-system check without a reboot; errors found are queued for repair. A few minutes."));
            t.Admin = true; t.Long = true;
            l.Add(t);
            t = Tool("wureset", ToolsFix, Tr.S("Сбросить компоненты Windows Update", "Reset Windows Update components"),
                Tr.S("Останавливает службы обновления, переименовывает SoftwareDistribution и catroot2, запускает службы снова: лечит вечное «поиск обновлений» и ошибки 0x8007xxxx. История обновлений очистится.",
                     "Stops the update services, renames SoftwareDistribution and catroot2, starts the services again: cures the endless “checking for updates” and 0x8007xxxx errors. Update history is cleared."));
            t.Confirm = true; t.Admin = true; t.Long = true;   // восемь net stop/start по 90 с + удаление каталогов
            t.ConfirmText = Tr.S("Сбросить компоненты Windows Update? Скачанные, но не установленные обновления и история обновлений будут удалены; сами обновления скачаются заново.",
                                 "Reset Windows Update components? Downloaded-but-not-installed updates and the update history are deleted; updates re-download on their own.");
            l.Add(t);
            t = Tool("restore", ToolsFix, Tr.S("Создать точку восстановления", "Create a restore point"),
                Tr.S("Снимок системных файлов, реестра и драйверов, к которому можно откатиться из «Восстановления системы». Личные файлы не трогает. Если защита системы выключена — включит её для системного диска.",
                     "A snapshot of system files, registry and drivers you can roll back to from System Restore. Personal files are untouched. Enables System Protection for the system drive if it is off."));
            t.Admin = true; t.Long = true;
            l.Add(t);
            t = Tool("hiber", ToolsFix, Tr.S("Гибернация: выключить / включить", "Hibernation: off / on"),
                Tr.S("powercfg /hibernate: выключение удаляет hiberfil.sys (обычно 40 % от объёма ОЗУ) и отключает быстрый запуск; включение возвращает обратно.",
                     "powercfg /hibernate: turning it off deletes hiberfil.sys (usually 40 % of RAM size) and disables fast startup; turning it on brings both back."));
            t.Confirm = true; t.Admin = true;
            l.Add(t);
            t = Tool("spooler", ToolsFix, Tr.S("Сбросить очередь печати", "Reset the print queue"),
                Tr.S("Останавливает диспетчер печати, удаляет застрявшие задания из spool\\PRINTERS и запускает снова: принтер «печатает» вечно или не отвечает.",
                     "Stops the print spooler, deletes stuck jobs from spool\\PRINTERS and starts it again: the printer “prints” forever or does not respond."));
            t.Confirm = true; t.Admin = true;
            t.ConfirmText = Tr.S("Очистить очередь печати? Все ожидающие задания печати будут удалены.",
                                 "Clear the print queue? All pending print jobs will be deleted.");
            t.Long = true;   // net stop spooler на зависшей очереди упирается в свои 60 с
            l.Add(t);
            t = Tool("storereset", ToolsFix, Tr.S("Сбросить кэш Microsoft Store", "Reset Microsoft Store cache"),
                Tr.S("wsreset.exe: чинит Store, который не открывается или не скачивает. Появится чёрное окно на несколько секунд, затем откроется Store.",
                     "wsreset.exe: fixes a Store that will not open or download. A black window shows for a few seconds, then the Store opens."));
            t.Open = "wsreset.exe";
            l.Add(t);
            l.Add(Tool("clipboard", ToolsFix, Tr.S("Очистить буфер обмена", "Clear the clipboard"),
                Tr.S("Удаляет содержимое буфера обмена — например, скопированный пароль или большой скриншот, который держит память.",
                     "Empties the clipboard — e.g. a copied password or a large screenshot that holds memory.")));

            // ---- защита ----
            t = Tool("defquick", ToolsProtect, Tr.S("Быстрая проверка Защитником", "Defender quick scan"),
                Tr.S("MpCmdRun -Scan -ScanType 1: проверка автозагрузки, памяти и системных папок. 1–10 минут, можно остановить.",
                     "MpCmdRun -Scan -ScanType 1: scans startup, memory and system folders. 1–10 minutes, can be stopped."));
            t.Long = true;
            l.Add(t);
            t = Tool("defsig", ToolsProtect, Tr.S("Обновить антивирусные базы", "Update antivirus definitions"),
                Tr.S("MpCmdRun -SignatureUpdate: скачивает свежие определения Защитника Windows, не дожидаясь расписания.",
                     "MpCmdRun -SignatureUpdate: downloads fresh Windows Defender definitions without waiting for the schedule."));
            t.Long = true;   // качает базы: на медленном канале это минуты, а не секунды
            l.Add(t);
            l.Add(Tool("wucheck", ToolsProtect, Tr.S("Проверить обновления Windows", "Check for Windows updates"),
                Tr.S("Запускает поиск обновлений (UsoClient) и открывает Центр обновления, где видно результат.",
                     "Starts an update scan (UsoClient) and opens Windows Update, where the result shows.")));
            l.Add(Opener("defopen", Tr.S("Открыть «Безопасность Windows»", "Open “Windows Security”"),
                Tr.S("Состояние защиты, история угроз, исключения, брандмауэр.", "Protection status, threat history, exclusions, firewall."), "windowsdefender:", null));

            // ---- инструменты Windows ----
            l.Add(Opener("cleanmgr", Tr.S("Очистка диска (Windows)", "Disk Cleanup (Windows)"), Tr.S("Штатная очистка: файлы обновлений, эскизы, журналы.", "Built-in cleanup: update files, thumbnails, logs."), "cleanmgr.exe", null));
            l.Add(Opener("storagesense", Tr.S("Параметры хранилища", "Storage settings"), Tr.S("Контроль памяти, что занимает место, куда сохранять новое.", "Storage Sense, what takes space, where new content goes."), "ms-settings:storagesense", null));
            l.Add(Opener("taskmgr", Tr.S("Диспетчер задач", "Task Manager"), Tr.S("Процессы, производительность, автозагрузка, службы.", "Processes, performance, startup, services."), "taskmgr.exe", null));
            l.Add(Opener("resmon", Tr.S("Монитор ресурсов", "Resource Monitor"), Tr.S("Кто грузит диск, сеть и память прямо сейчас.", "Who is loading the disk, network and memory right now."), "resmon.exe", null));
            l.Add(Opener("devmgmt", Tr.S("Диспетчер устройств", "Device Manager"), Tr.S("Устройства, драйверы, конфликты.", "Devices, drivers, conflicts."), "devmgmt.msc", null));
            l.Add(Opener("services", Tr.S("Службы", "Services"), Tr.S("Тип запуска и состояние всех служб Windows.", "Startup type and state of every Windows service."), "services.msc", null));
            l.Add(Opener("eventvwr", Tr.S("Просмотр событий", "Event Viewer"), Tr.S("Журналы ошибок системы и приложений.", "System and application error logs."), "eventvwr.msc", null));
            l.Add(Opener("reliability", Tr.S("Монитор стабильности", "Reliability Monitor"), Tr.S("Сбои и падения по дням — что и когда ломалось.", "Crashes and failures by day — what broke and when."), "perfmon.exe", "/rel"));
            l.Add(Opener("wupdate", Tr.S("Центр обновления Windows", "Windows Update"), Tr.S("Проверка, установка, приостановка обновлений.", "Check, install, pause updates."), "ms-settings:windowsupdate", null));
            l.Add(Opener("appwiz", Tr.S("Программы и компоненты", "Programs and Features"), Tr.S("Классическое удаление программ.", "Classic program uninstall."), "appwiz.cpl", null));
            l.Add(Opener("features", Tr.S("Компоненты Windows", "Windows Features"), Tr.S("Включение и отключение компонентов (Hyper-V, WSL, .NET 3.5…).", "Turn Windows features on or off (Hyper-V, WSL, .NET 3.5…)."), "optionalfeatures.exe", null));
            l.Add(Opener("sysrestore", Tr.S("Восстановление системы", "System Restore"), Tr.S("Откат к точке восстановления.", "Roll back to a restore point."), "rstrui.exe", null));
            l.Add(Opener("sysprotect", Tr.S("Защита системы", "System Protection"), Tr.S("Включить защиту, задать место под точки, удалить старые.", "Enable protection, set the space for points, delete old ones."), "SystemPropertiesProtection.exe", null));
            l.Add(Opener("startupapps", Tr.S("Автозагрузка (Параметры)", "Startup apps (Settings)"), Tr.S("Штатный список автозапуска с оценкой влияния.", "The built-in startup list with impact ratings."), "ms-settings:startupapps", null));
            l.Add(Opener("startupfolder", Tr.S("Папка «Автозагрузка»", "Startup folder"), Tr.S("Ярлыки, которые запускаются при входе.", "Shortcuts that run at sign-in."), "explorer.exe", "shell:startup"));
            l.Add(Opener("ncpa", Tr.S("Сетевые подключения", "Network Connections"), Tr.S("Адаптеры, их состояние и свойства.", "Adapters, their state and properties."), "ncpa.cpl", null));
            l.Add(Opener("powercfg", Tr.S("Электропитание", "Power Options"), Tr.S("Схемы питания, действие кнопок, спящий режим.", "Power plans, button actions, sleep."), "powercfg.cpl", null));
            l.Add(Opener("envvars", Tr.S("Переменные среды", "Environment Variables"), Tr.S("PATH и другие переменные пользователя и системы.", "PATH and other user and system variables."), "rundll32.exe", "sysdm.cpl,EditEnvironmentVariables"));
            l.Add(Opener("msconfig", Tr.S("Конфигурация системы", "System Configuration"), Tr.S("msconfig: службы, загрузка, безопасный режим.", "msconfig: services, boot, safe mode."), "msconfig.exe", null));
            l.Add(Opener("diskmgmt", Tr.S("Управление дисками", "Disk Management"), Tr.S("Разделы, буквы дисков, форматирование.", "Partitions, drive letters, formatting."), "diskmgmt.msc", null));
            l.Add(Opener("msinfo", Tr.S("Сведения о системе", "System Information"), Tr.S("msinfo32: железо, версия Windows, драйверы.", "msinfo32: hardware, Windows version, drivers."), "msinfo32.exe", null));
            l.Add(Opener("dxdiag", Tr.S("Средство диагностики DirectX", "DirectX Diagnostic Tool"), Tr.S("dxdiag: видеокарта, звук, версии DirectX.", "dxdiag: GPU, sound, DirectX versions."), "dxdiag.exe", null));
            l.Add(Opener("mdsched", Tr.S("Проверка памяти", "Memory Diagnostic"), Tr.S("Тест ОЗУ при следующей перезагрузке.", "RAM test at the next reboot."), "mdsched.exe", null));
            l.Add(Opener("regedit", Tr.S("Редактор реестра", "Registry Editor"), Tr.S("regedit — только если знаете, что делаете.", "regedit — only if you know what you are doing."), "regedit.exe", null));
            l.Add(Opener("control", Tr.S("Панель управления", "Control Panel"), Tr.S("Классические настройки.", "Classic settings."), "control.exe", null));
            l.Add(Opener("settings", Tr.S("Параметры Windows", "Windows Settings"), Tr.S("Современные настройки.", "Modern settings."), "ms-settings:", null));
            l.Add(Opener("terminal", Tr.S("PowerShell", "PowerShell"), Tr.S("Окно PowerShell (с правами приложения).", "A PowerShell window (with the app's rights)."), "powershell.exe", null));
            return l;
        }

        public static ToolItem FindTool(string id)
        {
            foreach (ToolItem t in ToolCatalog()) if (t.Id == id) return t;
            return null;
        }

        // Запуск без журнала: exe, .msc/.cpl через ассоциации оболочки, ms-settings:/windowsdefender: как URI.
        public static string ToolOpen(ToolItem t)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(t.Open);
                if (!string.IsNullOrEmpty(t.Args)) psi.Arguments = t.Args;
                psi.UseShellExecute = true;
                Process.Start(psi);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ================= ВЫПОЛНЕНИЕ =================
        // log получает строки по мере появления (вывод утилит идёт в журнал живьём: sfc и DISM работают
        // минутами, и пустое окно выглядело бы как зависание). cancel опрашивается во время ожидания.
        public bool ToolRun(string id, Action<string> log, Func<bool> cancel)
        {
            string sys = Environment.SystemDirectory;
            switch (id)
            {
                case "flushdns":
                    {
                        // /registerdns требует прав администратора: без них — только сброс кэша, и это успех.
                        bool a = Run(log, cancel, Path.Combine(sys, "ipconfig.exe"), "/flushdns", OemEncoding(), 60000) == 0;
                        if (!IsAdmin()) return a;
                        return Run(log, cancel, Path.Combine(sys, "ipconfig.exe"), "/registerdns", OemEncoding(), 60000) == 0 && a;
                    }
                case "netreset":
                    {
                        bool a = Run(log, cancel, Path.Combine(sys, "netsh.exe"), "winsock reset", OemEncoding(), 60000) == 0;
                        bool b = Run(log, cancel, Path.Combine(sys, "netsh.exe"), "int ip reset", OemEncoding(), 60000) == 0;
                        log(Tr.S("Перезагрузите компьютер, чтобы сброс вступил в силу.", "Reboot the computer for the reset to take effect."));
                        return a && b;
                    }
                case "explorer":
                    return RestartExplorer(log, cancel, null);
                case "iconcache":
                    return RestartExplorer(log, cancel, delegate
                    {
                        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Explorer");
                        int n = DeleteMatching(dir, "iconcache_*.db", log);
                        string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IconCache.db");
                        try { if (File.Exists(legacy)) { File.Delete(legacy); n++; } } catch (Exception ex) { log("  " + legacy + ": " + ex.Message); }
                        log(Tr.S("Удалено файлов кэша значков: ", "Icon cache files deleted: ") + n);
                        Run(log, cancel, Path.Combine(sys, "ie4uinit.exe"), "-show", OemEncoding(), 30000);
                    });
                case "fontcache":
                    {
                        Run(log, cancel, Path.Combine(sys, "net.exe"), "stop FontCache", OemEncoding(), 60000);
                        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                        int n = DeleteMatching(Path.Combine(win, @"ServiceProfiles\LocalService\AppData\Local\FontCache"), "*.dat", log);
                        string fnt = Path.Combine(sys, "FNTCACHE.DAT");
                        try { if (File.Exists(fnt)) { File.Delete(fnt); n++; } } catch (Exception ex) { log("  " + fnt + ": " + ex.Message); }
                        log(Tr.S("Удалено файлов кэша шрифтов: ", "Font cache files deleted: ") + n);
                        return NetOk(Run(log, cancel, Path.Combine(sys, "net.exe"), "start FontCache", OemEncoding(), 60000), log);
                    }
                case "sfc":
                    // sfc пишет в перенаправленный вывод UTF-16 — с любой другой кодировкой это «п р о б е л ы».
                    return Run(log, cancel, Path.Combine(sys, "sfc.exe"), "/scannow", Encoding.Unicode, 40 * 60000) == 0;
                case "dism":
                    return Run(log, cancel, DismPath(), "/Online /Cleanup-Image /RestoreHealth /English", null, 60 * 60000) == 0;
                case "chkdsk":
                    {
                        string drive = SystemDriveRoot().TrimEnd('\\');
                        int code = Run(log, cancel, Path.Combine(sys, "chkdsk.exe"), drive + " /scan", OemEncoding(), 60 * 60000);
                        // 0 = ошибок нет, 1 = найдены и помечены для исправления — оба «выполнено»
                        return code == 0 || code == 1;
                    }
                case "wureset":
                    return ResetWindowsUpdate(log, cancel);
                case "restore":
                    return CreateRestorePoint(log, cancel);
                case "hiber":
                    {
                        long size;
                        bool on = HibernationEnabled(out size);
                        int code = Run(log, cancel, Path.Combine(sys, "powercfg.exe"), on ? "/hibernate off" : "/hibernate on", OemEncoding(), 60000);
                        if (code == 0) log(on ? Tr.S("Гибернация выключена, hiberfil.sys удалён.", "Hibernation is off, hiberfil.sys deleted.")
                                            : Tr.S("Гибернация включена.", "Hibernation is on."));
                        return code == 0;
                    }
                case "spooler":
                    {
                        Run(log, cancel, Path.Combine(sys, "net.exe"), "stop spooler", OemEncoding(), 60000);
                        int n = DeleteMatching(Path.Combine(sys, @"spool\PRINTERS"), "*", log);
                        log(Tr.S("Удалено файлов очереди: ", "Queue files deleted: ") + n);
                        return NetOk(Run(log, cancel, Path.Combine(sys, "net.exe"), "start spooler", OemEncoding(), 60000), log);
                    }
                case "defquick":
                    // «Быстрая проверка» идёт 1–10 минут; час ожидания означал только одно —
                    // при зависшем MpCmdRun вкладка держалась занятой до конца рабочего дня.
                    return Run(log, cancel, MpCmdRunPath(), "-Scan -ScanType 1", null, 15 * 60000) == 0;
                case "defsig":
                    return Run(log, cancel, MpCmdRunPath(), "-SignatureUpdate", null, 10 * 60000) == 0;
                case "wucheck":
                    {
                        Run(log, cancel, Path.Combine(sys, "UsoClient.exe"), "StartInteractiveScan", OemEncoding(), 30000);
                        log(Tr.S("Поиск обновлений запущен; результат — в Центре обновления.", "Update scan started; see Windows Update for the result."));
                        ToolItem wu = FindTool("wupdate");
                        string err = ToolOpen(wu);
                        if (err != null) log(err);
                        return true;
                    }
                default:
                    log(Tr.S("Неизвестный инструмент: ", "Unknown tool: ") + id);
                    return false;
            }
        }

        private static string MpCmdRunPath()
        {
            // Копия в Program Files — заглушка старой версии; актуальная платформа лежит в ProgramData.
            try
            {
                string plat = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows Defender\Platform");
                if (Directory.Exists(plat))
                {
                    string best = null; Version bestV = null;
                    foreach (string d in Directory.GetDirectories(plat))
                    {
                        Version v;
                        string name = Path.GetFileName(d);
                        int dash = name.IndexOf('-');
                        if (!Version.TryParse(dash > 0 ? name.Substring(0, dash) : name, out v)) continue;
                        if (bestV == null || v > bestV) { bestV = v; best = d; }
                    }
                    if (best != null && File.Exists(Path.Combine(best, "MpCmdRun.exe"))) return Path.Combine(best, "MpCmdRun.exe");
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
        }

        // Строки вывода по мере появления. stdout и stderr читаются асинхронно, прогресс-обновления
        // через \r приходят отдельными строками. Возвращает код выхода, RunTimeout или RunCancelled.
        private static int Run(Action<string> log, Func<bool> cancel, string exe, string args, Encoding enc, int timeoutMs)
        {
            log("> " + Path.GetFileName(exe) + " " + args);
            if (!File.Exists(exe)) { log(Tr.S("  не найден: ", "  not found: ") + exe); return -1; }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = enc ?? Encoding.UTF8;
                psi.StandardErrorEncoding = enc ?? Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) { log(Tr.S("  не запустился", "  failed to start")); return -1; }
                    DataReceivedEventHandler h = delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        string line = e.Data.Replace("\0", "").TrimEnd();
                        if (line.Length > 0) log("  " + line);
                    };
                    p.OutputDataReceived += h;
                    p.ErrorDataReceived += h;
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    Stopwatch sw = Stopwatch.StartNew();
                    while (!p.WaitForExit(250))
                    {
                        if (cancel != null && cancel())
                        {
                            try { p.Kill(); } catch { }
                            log(Tr.S("  остановлено", "  cancelled"));
                            return RunCancelled;
                        }
                        if (sw.ElapsedMilliseconds > timeoutMs)
                        {
                            try { p.Kill(); } catch { }
                            log(Tr.S("  не уложился в отведённое время", "  timed out"));
                            return RunTimeout;
                        }
                    }
                    // Дочитать асинхронные буферы, но не дольше пяти секунд: WaitForExit() без срока
                    // ждёт закрытия труб, а их может держать внук уже завершившегося процесса —
                    // тогда вкладка «Инструменты» вставала навсегда после успешно отработавшей команды.
                    p.WaitForExit(5000);
                    log(Tr.S("  код выхода: ", "  exit code: ") + p.ExitCode);
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { log("  " + ex.Message); return -1; }
        }

        // net start / net stop возвращает 2, когда служба уже в нужном состоянии («служба уже запущена»).
        // Раньше это считалось провалом, и полностью успешный сброс кэша шрифтов или очереди печати
        // заканчивался красным «не выполнено».
        private static bool NetOk(int code, Action<string> log)
        {
            if (code == 0) return true;
            if (code == 2) { log(Tr.S("  служба уже в нужном состоянии — это успех", "  the service is already in the required state — this is success")); return true; }
            return false;
        }

        private static int DeleteMatching(string dir, string mask, Action<string> log)
        {
            int n = 0;
            try
            {
                if (!Directory.Exists(dir)) return 0;
                foreach (string f in Directory.GetFiles(dir, mask))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); n++; }
                    catch (Exception ex) { log("  " + f + ": " + ex.Message); }
                }
            }
            catch (Exception ex) { log("  " + dir + ": " + ex.Message); }
            return n;
        }

        // Проводник: завершить, выполнить between (пока он не держит файлы), дождаться автоперезапуска
        // (winlogon поднимает оболочку сам, AutoRestartShell), иначе запустить самим.
        private static bool RestartExplorer(Action<string> log, Func<bool> cancel, Action between)
        {
            string sys = Environment.SystemDirectory;
            Run(log, cancel, Path.Combine(sys, "taskkill.exe"), "/F /IM explorer.exe", OemEncoding(), 30000);
            Thread.Sleep(800);
            if (between != null) { try { between(); } catch (Exception ex) { log("  " + ex.Message); } }
            log(Tr.S("Жду автозапуск оболочки…", "Waiting for the shell to restart…"));
            for (int i = 0; i < 12; i++)
            {
                Thread.Sleep(500);
                if (Process.GetProcessesByName("explorer").Length > 0)
                {
                    log(Tr.S("Проводник запущен снова.", "Explorer is running again."));
                    return true;
                }
                // Отмена посреди ожидания: раньше эти шесть секунд игнорировали «Остановить».
                // Проводник всё равно поднимаем — оставить рабочий стол без оболочки нельзя.
                if (cancel != null && cancel()) { log(Tr.S("Остановка — запускаю Проводник сразу.", "Cancelling — starting Explorer right away.")); break; }
            }
            try
            {
                Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
                log(Tr.S("Проводник запущен вручную.", "Explorer started manually."));
                return true;
            }
            catch (Exception ex) { log("  explorer.exe: " + ex.Message); return false; }
        }

        private static bool ResetWindowsUpdate(Action<string> log, Func<bool> cancel)
        {
            string sys = Environment.SystemDirectory;
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string[] svcs = { "bits", "wuauserv", "cryptsvc", "msiserver" };
            foreach (string s in svcs) Run(log, cancel, Path.Combine(sys, "net.exe"), "stop " + s, OemEncoding(), 90000);
            bool ok = true;
            string[] dirs = { Path.Combine(win, "SoftwareDistribution"), Path.Combine(sys, "catroot2") };
            List<string> olds = new List<string>();
            foreach (string d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                string old = d + ".old";
                try
                {
                    if (Directory.Exists(old)) Directory.Delete(old, true);
                    Directory.Move(d, old);
                    olds.Add(old);
                    log(Tr.S("Переименовано: ", "Renamed: ") + d + " -> " + Path.GetFileName(old));
                }
                catch (Exception ex) { ok = false; log("  " + d + ": " + ex.Message); }
            }
            foreach (string s in new string[] { "cryptsvc", "bits", "wuauserv", "msiserver" })
                Run(log, cancel, Path.Combine(sys, "net.exe"), "start " + s, OemEncoding(), 90000);
            foreach (string old in olds)
            {
                try { Directory.Delete(old, true); log(Tr.S("Удалена старая копия: ", "Old copy deleted: ") + old); }
                catch (Exception ex) { log("  " + old + Tr.S(": не удалена (", ": not deleted (") + ex.Message + ")"); }
            }
            log(ok ? Tr.S("Готово. Откройте Центр обновления и запустите поиск обновлений.", "Done. Open Windows Update and check for updates.")
                   : Tr.S("Выполнено с ошибками — см. выше.", "Finished with errors — see above."));
            return ok;
        }

        // Точка восстановления через Checkpoint-Computer. Windows создаёт не чаще одной точки в сутки
        // (SystemRestorePointCreationFrequency) — на время вызова лимит снимается и возвращается как был.
        private bool CreateRestorePoint(Action<string> log, Func<bool> cancel)
        {
            if (!IsAdmin()) { log(Tr.S("Нужны права администратора.", "Administrator rights are required.")); return false; }
            string drive = SystemDriveRoot();
            string script = "";
            if (!SystemRestoreEnabled())
            {
                log(Tr.S("Защита системы выключена — включаю для ", "System Protection is off — enabling for ") + drive);
                script += "Enable-ComputerRestore -Drive " + PsQuote(drive) + " -ErrorAction Stop; ";
            }
            string desc = "Windows Process Cleaner " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            script +=
                "$k='HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore';" +
                "$old=(Get-ItemProperty $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency;" +
                "Set-ItemProperty $k -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord;" +
                "try{Checkpoint-Computer -Description " + PsQuote(desc) + " -RestorePointType MODIFY_SETTINGS -ErrorAction Stop;'OK'}" +
                "catch{'ERR '+$_.Exception.Message}" +
                "finally{if($null -eq $old){Remove-ItemProperty $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue}" +
                "else{Set-ItemProperty $k -Name SystemRestorePointCreationFrequency -Value $old -Type DWord}}";
            log(Tr.S("Создаю точку восстановления «", "Creating restore point “") + desc + Tr.S("»… обычно 10–60 секунд.", "”… usually 10–60 seconds."));
            string so; int code;
            // Отмена доходит до самого PowerShell: Checkpoint-Computer на занятом диске держит
            // до десяти минут, и всё это время «Остановить» была бесполезной кнопкой.
            bool ran = PS(script, 10 * 60000, out so, out code, cancel, null);
            foreach (string line in (so ?? "").Split('\n')) { string l = line.Trim(); if (l.Length > 0) log("  " + l); }
            bool ok = ran && so != null && so.IndexOf("OK", StringComparison.Ordinal) >= 0 && so.IndexOf("ERR ", StringComparison.Ordinal) < 0;
            log(ok ? Tr.S("Точка восстановления создана.", "Restore point created.") : Tr.S("Не удалось создать точку восстановления.", "Could not create a restore point."));
            return ok;
        }
    }
}
