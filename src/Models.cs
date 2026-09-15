// SysDeck — конфигурация, история, модели данных (процессы, порты, очистка, обновления, автозапуск)
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

namespace SysDeck
{
    // ------------------------------------------------------------------ //
    //  Конфигурация и история (сериализуются в JSON)
    // ------------------------------------------------------------------ //
    [DataContract]
    public class AppConfig
    {
        [DataMember] public double CpuThresholdPercent;   // порог CPU %
        [DataMember] public int IdleMinutes;              // время простоя, мин
        [DataMember] public int MinLifetimeMinutes;       // мин. время жизни процесса, мин
        [DataMember] public int AutoIntervalHours;        // период автоочистки, 1..24
        [DataMember] public bool AutoEnabled;             // включена ли автоочистка
        [DataMember] public bool Autostart;               // автозапуск с Windows
        [DataMember] public bool StartMinimized;          // стартовать свёрнутым в трей
        [DataMember] public string Theme;                 // "system" | "light" | "dark"
        [DataMember] public bool GlobalScan;              // сканировать ВСЕ процессы, не только dev
        [DataMember] public int GlobalIdleMinutes;        // мин. простой для глобального режима (безопасность)
        [DataMember] public bool GlobalExcludeInstalled;  // не трогать установленный софт (Program Files)
        [DataMember] public string Language;              // "ru" | "en"
        [DataMember] public List<string> Watchlist;       // отслеживаемые процессы
        [DataMember] public List<string> Whitelist;       // белый список (не трогать)
        [DataMember] public List<int> DevPorts;           // популярные dev-порты
        [DataMember] public int MonitorIntervalSeconds;   // период тика мониторинга, 5..300
        [DataMember] public bool MonitorEnabled;          // фоновый мониторинг CPU вообще нужен
        [DataMember] public bool EmptyWorkingSets;        // сбрасывать рабочие наборы всех процессов
        [DataMember] public int CleanSkipRecentMinutes;   // не удалять файлы, изменённые за последние N мин
        [DataMember] public bool CleanLogEnabled;         // писать лог очистки
        [DataMember] public List<string> CleanExclude;    // пути, которые никогда не чистить
        [DataMember] public List<string> CleanUnchecked;  // элементы состава категорий, снятые пользователем (ключи)
        [DataMember] public List<string> UpdateExclude;   // Id пакетов, которые не предлагать к обновлению
        [DataMember] public bool UpdateIncludeUnknown;    // показывать пакеты с неопределённой текущей версией
        [DataMember] public bool UpdateUseChoco;          // опрашивать Chocolatey, если он установлен
        [DataMember] public int UpdateBatchSize;           // сколько пакетов отдавать менеджеру одной командой
        [DataMember] public int DiskMinMb;                 // вкладка «Диск»: порог крупных файлов и дубликатов, МБ (1..10240)
        [DataMember] public bool SmartBoostEnabled;        // «умное ускорение»: чистить Standby Memory, когда RAM занята сильнее порога
        [DataMember] public int SmartBoostPercent;         // порог занятости RAM, % (50..99)
        // Что пользователь отметил или выбрал в окне: «область \t ключ \t значение».
        // Одно поле на все вкладки — см. MainForm.Memory.cs; список ограничен UiChecksMax,
        // иначе записи об исчезнувших пакетах и программах копились бы вечно.
        [DataMember] public List<string> UiChecks;
        // Версия схемы: отличает "поле отсутствует в старом config.json" (bool => false)
        // от "пользователь выключил". Без неё апгрейд молча гасит новые флаги.
        [DataMember] public int ConfigVersion;

        public static AppConfig Default()
        {
            AppConfig c = new AppConfig();
            c.CpuThresholdPercent = 0.1;
            c.IdleMinutes = 5;
            c.MinLifetimeMinutes = 5;
            c.AutoIntervalHours = 4;
            c.AutoEnabled = false;
            c.Autostart = false;
            c.StartMinimized = false;
            c.Theme = "system";
            c.GlobalScan = false;
            c.GlobalIdleMinutes = 30;
            c.GlobalExcludeInstalled = true;
            c.Language = "ru";
            c.Watchlist = new List<string>(new string[] {
                "node.exe","npm.exe","pnpm.exe","yarn.exe","bun.exe",
                "python.exe","pythonw.exe","java.exe","gradle.exe",
                "vite.exe","webpack.exe","next.exe","cargo.exe",
                "go.exe","deno.exe","ruby.exe","php.exe"
            });
            c.Whitelist = new List<string>(new string[] {
                "explorer.exe","wininit.exe","svchost.exe","dwm.exe","System","Registry",
                "docker.exe","com.docker.backend.exe","vmmem.exe","wsl.exe",
                "postgres.exe","mysqld.exe","redis-server.exe",
                "steam.exe","discord.exe","chrome.exe","firefox.exe","msedge.exe"
            });
            c.DevPorts = new List<int>(new int[] {
                3000,3001,3002,4173,5173,5174,8080,8000,8888,4200,4300,5000,5555,9000,9090,1337,19006
            });
            c.MonitorIntervalSeconds = 15;
            c.MonitorEnabled = true;
            // Сброс рабочих наборов выключен по умолчанию: он выдавливает страницы
            // ВСЕХ процессов, после чего система заметно тормозит, пока они грузятся обратно.
            c.EmptyWorkingSets = false;
            c.CleanSkipRecentMinutes = 10;
            c.CleanLogEnabled = true;
            c.CleanExclude = new List<string>();
            c.CleanUnchecked = new List<string>();
            c.UpdateExclude = new List<string>();
            c.UpdateIncludeUnknown = true;
            c.UpdateUseChoco = true;
            c.UpdateBatchSize = 5;
            c.DiskMinMb = 1;
            c.SmartBoostEnabled = true;
            c.SmartBoostPercent = 90;
            c.UiChecks = new List<string>();
            c.ConfigVersion = CurrentVersion;
            return c;
        }

        public const int CurrentVersion = 5;
        public const int UiChecksMax = 4000;

        public void Normalize()
        {
            if (Watchlist == null) Watchlist = Default().Watchlist;
            if (Whitelist == null) Whitelist = Default().Whitelist;
            if (DevPorts == null) DevPorts = Default().DevPorts;
            if (CleanExclude == null) CleanExclude = new List<string>();
            if (CleanUnchecked == null) CleanUnchecked = new List<string>();
            if (UpdateExclude == null) UpdateExclude = new List<string>();
            if (UiChecks == null) UiChecks = new List<string>();
            if (UiChecks.Count > UiChecksMax) UiChecks.RemoveRange(0, UiChecks.Count - UiChecksMax);
            // Миграции строго по одной ступени и БЕЗ присваивания CurrentVersion внутри:
            // иначе конфиг версии 0 перескочит на текущую, пропустив дефолты следующих ступеней.
            if (ConfigVersion < 1)
            {
                // конфиг от старой сборки: включаем новые возможности по умолчанию
                MonitorEnabled = true;
                CleanLogEnabled = true;
                CleanSkipRecentMinutes = 10;
            }
            if (ConfigVersion < 2)
            {
                UpdateIncludeUnknown = true;
                UpdateUseChoco = true;
            }
            if (ConfigVersion < 3)
            {
                UpdateBatchSize = 5;
            }
            if (ConfigVersion < 4)
            {
                // умное ускорение выключено по умолчанию: порог задаём, флаг оставляем снятым
                SmartBoostPercent = 90;
            }
            if (ConfigVersion < 5)
            {
                // память выбора заводится пустой: пока пользователь ничего не отмечал,
                // каждая вкладка показывает свои прежние умолчания
                UiChecks = new List<string>();
            }
            ConfigVersion = CurrentVersion;
            // 1 = по одному (точный статус из кода возврата); больше 20 в одной команде
            // не даёт выигрыша и растягивает срок, за который непонятно, что происходит.
            if (UpdateBatchSize < 1) UpdateBatchSize = 1;
            if (UpdateBatchSize > 20) UpdateBatchSize = 20;
            // отсутствующее поле в старом config.json читается как 0 — это и есть «по умолчанию 1 МБ»
            if (DiskMinMb < 1) DiskMinMb = 1;
            if (DiskMinMb > 10240) DiskMinMb = 10240;
            if (SmartBoostPercent < 50 || SmartBoostPercent > 99) SmartBoostPercent = 90;
            if (MonitorIntervalSeconds < 5) MonitorIntervalSeconds = 15;
            if (MonitorIntervalSeconds > 300) MonitorIntervalSeconds = 300;
            if (CleanSkipRecentMinutes < 0) CleanSkipRecentMinutes = 0;
            if (string.IsNullOrEmpty(Theme)) Theme = "system";
            if (string.IsNullOrEmpty(Language)) Language = "ru";
            if (GlobalIdleMinutes < 1) GlobalIdleMinutes = 30;
            if (AutoIntervalHours < 1) AutoIntervalHours = 1;
            if (AutoIntervalHours > 24) AutoIntervalHours = 24;
            if (IdleMinutes < 0) IdleMinutes = 0;
            if (MinLifetimeMinutes < 0) MinLifetimeMinutes = 0;
            if (CpuThresholdPercent < 0) CpuThresholdPercent = 0;
        }
    }

    [DataContract]
    public class HistoryEntry
    {
        [DataMember] public string DateTime;
        [DataMember] public int TerminatedCount;
        [DataMember] public long FreedBytes;
        [DataMember] public List<string> Processes;
    }

    [DataContract]
    public class HistoryFile
    {
        [DataMember] public List<HistoryEntry> Entries;
    }

    // ------------------------------------------------------------------ //
    //  Модель найденного процесса
    // ------------------------------------------------------------------ //
    public class ProcInfo
    {
        public int Pid;
        public int ParentPid;
        public string Name;        // node.exe
        public string Category;    // Node.js
        public string Path;
        public TimeSpan Uptime;
        public double CpuPercent;
        public long RamBytes;
        public bool HasWindow;
        public bool ListensTcp;
        public bool HasChildren;
        public bool ParentAlive;
        public TimeSpan IdleFor;
        public bool Whitelisted;
        public bool UserOwned;     // принадлежит текущему пользователю
        public bool IsSystemPath;  // лежит в системной папке Windows
        public bool IsCandidate;   // кандидат на завершение
        public string Reason;      // почему кандидат / почему нет
    }

    // ------------------------------------------------------------------ //
    //  Строка занятого порта (для Dev Cleanup)
    // ------------------------------------------------------------------ //
    public class PortRow
    {
        public int Port;
        public int Pid;
        public string ProcName;
    }

    // Категория очистки диска (набор известных мусорных путей).
    public class CleanTarget
    {
        public string Path;
        public bool ContentsOnly;        // удалять содержимое, саму папку оставить
        public string Mask;              // маска файлов, null = все (для winapp2-правил и thumbcache_*.db)
        public bool Recurse = true;      // спускаться в подпапки
        public int MinAgeMinutes;        // не трогать файлы, изменённые за последние N минут (0 = без фильтра)
        // Результат анализа именно этой цели — чтобы в составе категории было видно,
        // сколько весит каждая папка, а не только сумма.
        public long Size;
        public int FileCount;
        public int Errors;               // папок/файлов, к которым нет доступа
        public bool Analyzed;
        public bool Guarded;             // отсечена предохранителем IsAllowedTarget
        // Цель пришла из внешнего файла правил (winapp2.ini), а не из кода. Такой цели
        // достаётся более строгий предохранитель: файл правил может подменить кто угодно,
        // а приложение работает от администратора.
        public bool FromRules;
        // Остатки обновлений Windows (Windows.old, $Windows.~BT, ESD) принадлежат TrustedInstaller:
        // администратору их удалять нельзя, пока он не станет владельцем. Такой цели при
        // «отказано в доступе» разрешено взять владение и повторить удаление.
        public bool TakeOwnership;
        // Проверено автором правила: содержимое цели приложение создаёт заново само. Только такой
        // цели разрешено удалять в общесистемном дереве (%ProgramData%, Program Files) файлы любого
        // вида; без флага там удаляется лишь заведомо одноразовое — см. Engine.IsThrowawayInAppTree.
        public bool Disposable;
        // Выбор пользователя: снятая галочка в «Составе». Ключ стабилен между запусками.
        public bool Enabled = true;
        public string Key;
    }
    // Пакет драйвера в DriverStore — кандидат на удаление через pnputil.
    public class DriverPackage
    {
        public string Published;         // oemNN.inf — под этим именем pnputil его удаляет
        public string Original;          // nv_dispi.inf — исходное имя, по нему группируются версии
        public string Provider;
        public string ClassName;
        public string Version;           // 32.0.15.8123
        public bool VersionKnown = true; // false — строку «дата версия» разобрать не удалось: сравнивать нечем, пакет не трогаем
        public DateTime Date;
        public string RepoDir;           // папка в DriverStore\FileRepository, если сопоставилась
        public long Size;
        public bool InUse;               // привязан к присутствующему устройству
        public bool Enabled = true;      // снятая галочка в «Составе» — пакет остаётся
    }
    public class CleanCategory
    {
        public string Id;
        public string Title;
        public string Desc;
        public List<CleanTarget> Targets = new List<CleanTarget>();
        public bool RecycleBin;
        public bool Recommended;
        public long Size;
        public int FileCount;
        public bool Analyzed;
        public string Note;              // почему пусто / что не удалось посчитать
        // Живой статус, пока категория считается или чистится: его показывает вместо «…»
        // колонка размера. Пишет фоновый поток, читает UI-таймер — гонка тут безобидна.
        public volatile string Progress;
        // Категории-действия: у них нет папок, размер и удаление считает внешняя утилита.
        // null = обычные папки; "driverstore" = pnputil; "winsxs" = DISM.
        public string Kind;
        public List<DriverPackage> Drivers;   // для "driverstore": что именно удалять
        // Что пользователь отключил в «Составе»: не входит в Size и не удаляется
        public long SizeOff;
        public int TargetsOff;
        // Корзина считается отдельным элементом состава — её можно оставить, не отказываясь от категории
        public bool BinEnabled = true;
        public long BinSize;
        public int BinCount;
    }
    public class CleanResult
    {
        public long Freed;
        public int Errors;
        public int FilesDeleted;
        // Остановлено пользователем: итог неполный, и говорить об этом должен сам итог,
        // а не догадка окна по флагу отмены, который к тому времени уже сброшен.
        public bool Cancelled;
        public List<string> Log = new List<string>();
    }

    // Доступное обновление программы (winget / Chocolatey).
    public class UpdateItem
    {
        public string Name;         // отображаемое имя, как его знает менеджер пакетов
        public string Id;           // идентификатор пакета — им и обновляем
        public string Current;      // установленная версия ("Unknown" / "< 1.2" бывают)
        public string Available;    // версия, доступная в источнике
        public string Manager;      // "winget" | "choco"
        public string Source;       // источник внутри менеджера (winget/msstore/...)
        public bool Duplicate;      // тот же софт уже виден через другой менеджер
        public string Status;        // результат последней попытки обновления
        public bool LastOk;         // удалась ли последняя попытка (не выводить из текста Status)

        // Насколько велик скачок версии: 3 крупное, 2 среднее, 1 мелкое, 0 неизвестно.
        // Это масштаб по номеру версии, а НЕ оценка безопасности: ни winget, ни
        // Chocolatey не отдают CVE/severity, вывести настоящую критичность из их
        // данных нельзя, и притворяться, что можно, было бы обманом.
        public int SeverityLevel;
        public string SeverityText;  // подпись для колонки «Важность»
    }

    // Установленная программа (для деинсталляции / автозапуска).
    public class InstalledApp
    {
        public string Name;
        public string Version;
        public string Publisher;
        public string UninstallCmd;
        public string QuietCmd;
        public string ExePath;   // главный exe (из DisplayIcon), если удалось определить
        public long EstimatedSizeBytes;
        public bool InAutostart; // вычисляется во вкладке автозапуска
        public string RegKey;    // раздел Uninstall, откуда взята запись (HKLM\… или HKCU\…): пропал — деинсталляция завершилась
        public string InstallLocation;
    }

    // Запись автозапуска (реестр Run или папка «Автозагрузка»).
    public class AutostartEntry
    {
        public string Name;
        public string Command;
        public string ExePath;
        public string SourceLabel;
        public int Kind;        // 0 HKCU Run, 1 HKLM Run, 2 HKLM WOW Run, 3 Startup(user), 4 Startup(common)
        public string RegName;  // имя значения в реестре
        public string LnkPath;  // путь к ярлыку в папке автозагрузки
        public bool Enabled = true;     // Windows запустит запись при входе: бит 0 флага StartupApproved снят или записи о состоянии нет
        public int ApprovedState = -1;  // байт 0 значения StartupApproved; -1 = записи о состоянии нет
    }
}
