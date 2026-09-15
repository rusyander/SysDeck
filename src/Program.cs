// SysDeck
// Единый файл. Компилируется встроенным в Windows csc.exe (.NET Framework 4.x).
// Никакой сторонней установки не требуется. См. build.bat / run.bat.
//
// Возможности:
//  - Поиск забытых процессов разработки (node/python/java/vite/webpack/...).
//  - Критерии "заброшенности": мёртвый родитель, простой CPU, нет окон, нет
//    слушающих TCP-портов, нет дочерних процессов, белый список, мин. время жизни.
//  - Корректное завершение (WM_CLOSE) -> ожидание 3с -> принудительно (Kill).
//  - Очистка Standby Memory через ntdll!NtSetSystemInformation (нужны права админа).
//  - Dev Cleanup: массовое завершение по группам + занятые dev-порты (IPv4 и IPv6).
//  - Очистка диска: категории мусора с составом (любую папку можно исключить навсегда),
//    правила winapp2.ini, старые пакеты драйверов (pnputil), WinSxS (DISM).
//  - Диск: карта папок с размерами, крупные файлы, пустые папки, дубликаты (SHA-256);
//    удаление только в Корзину (SHFileOperation). Ключ /disk [путь].
//  - Браузеры (Chromium): закладки, группы вкладок, сеансы, проверка ссылок.
//  - Docker: prune и сжатие vhdx. Программы. Обновления (winget/choco). Автозапуск.
//  - Таймер автоочистки процессов: каждые N часов (1..24), сохраняется в конфиге.
//  - Системный трей с индикацией активности и меню.
//  - Автозапуск вместе с Windows через планировщик задач (schtasks, с правами админа).
//  - История очисток и настройки в JSON (%APPDATA%\SysDeck); запись
//    атомарная (tmp + Replace), битый config.json откладывается как .corrupt.
//  - Single-instance: именованный Mutex; повторный запуск показывает окно первого
//    экземпляра через локальный TCP-порт 49876 (с /disk <путь> — и передаёт ему путь).
//  - Ключи: /tray (свернуть в трей), /auto (тихая очистка диска), /analyze (только отчёт),
//    /disk [путь] (открыть вкладку «Диск» и сразу просканировать путь).

// SysDeck — точка входа, локализация, single-instance
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
    //  Локализация: Tr.S("русский", "english") возвращает строку по языку.
    // ------------------------------------------------------------------ //
    internal static class Tr
    {
        public static bool En;
        public static string S(string ru, string en) { return En ? en : ru; }

        // Число + существительное с правильной формой: 1 файл / 2 файла / 5 файлов.
        public static string N(long n, string ru1, string ru2, string ru5, string en1, string en5)
        {
            string s = n.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
            if (En) return s + " " + (n == 1 ? en1 : en5);
            long a = Math.Abs(n) % 100, b = a % 10;
            string w = a >= 11 && a <= 19 ? ru5 : b == 1 ? ru1 : b >= 2 && b <= 4 ? ru2 : ru5;
            return s + " " + w;
        }
        public static string Files(long n) { return N(n, "файл", "файла", "файлов", "file", "files"); }
        public static string Folders(long n) { return N(n, "папка", "папки", "папок", "folder", "folders"); }
    }

    // ------------------------------------------------------------------ //
    //  Точка входа + single-instance через локальный порт
    // ------------------------------------------------------------------ //
    static class Program
    {
        private const int SingleInstancePort = 49876; // обычно свободный порт
        private static MainForm _form;
        private static Mutex _instanceMutex;          // держим ссылку: иначе GC соберёт и снимет владение

        [STAThread]
        static void Main(string[] args)
        {
            // --elevated-job <папка> — это не окно, а элевированный помощник: выполняет одну
            // операцию, пишет результат в файл и умирает. Проверяется раньше всего остального:
            // ни мьютекс единственного экземпляра, ни канал активации ему не нужны, а мешать
            // работающему окну (оно его и запустило) он не должен тем более.
            if (args != null)
                for (int i = 0; i + 1 < args.Length; i++)
                    if (args[i] == Elevation.JobSwitch)
                    {
                        Environment.ExitCode = Elevation.Execute(args[i + 1]);
                        return;
                    }

            // Переезд со старого имени (папка данных, ключи автозапуска) — раньше любого режима, который читает данные.
            // Хосту расширения некогда ждать остановки старых процессов: браузер ждёт ответа, он работает со старой папкой.
            if (!SysDeck.Downloads.DlBridge.IsHostLaunch(args)) Rebrand.MigrateOnStart();

            // --foldersize* — «Размеры папок»: фоновый режим со своим значком в трее и мьютексом
            // или консольная подкоманда. Окну и его единственному экземпляру не мешает.
            int folderSizeCode;
            if (SysDeck.FolderSize.FsMode.TryRun(args, out folderSizeCode))
            {
                Environment.ExitCode = folderSizeCode;
                return;
            }

            // --capture* — «Захват»: фоновый процесс снимков экрана (обычные права, свой мьютекс) или команда ему.
            int captureCode;
            if (SysDeck.Capture.CapMode.TryRun(args, out captureCode))
            {
                Environment.ExitCode = captureCode;
                return;
            }

            // --hud — оверлей показателей: свой резидент (может работать с правами через задачу Планировщика).
            int hudCode;
            if (SysDeck.Capture.HudMode.TryRun(args, out hudCode))
            {
                Environment.ExitCode = hudCode;
                return;
            }

            // Запуск браузером как native messaging host (chrome-extension://… или манифест + id дополнения Firefox):
            // кадры по stdin/stdout, окна нет, мьютекс окна не берётся — браузер держит хост, пока открыт порт расширения.
            int hostCode;
            if (SysDeck.Downloads.DlBridge.TryRun(args, out hostCode))
            {
                Environment.ExitCode = hostCode;
                return;
            }

            // --downloads* — «Загрузки»: фоновый процесс загрузок (обычные права, свой мьютекс) или команда ему.
            int downloadsCode;
            if (SysDeck.Downloads.DlMode.TryRun(args, out downloadsCode))
            {
                Environment.ExitCode = downloadsCode;
                return;
            }

            bool startTray = args != null && args.Contains("/tray");

            // /auto — тихая очистка диска без окна, для планировщика задач
            // (тот же сценарий, что /AUTO у FluentCleaner). Работает и когда основной
            // экземпляр уже запущен, поэтому проверяется до захвата single-instance.
            if (args != null && (args.Contains("/auto") || args.Contains("/AUTO")))
            {
                // Код возврата виден планировщику в «Последний результат выполнения»:
                // без него неудачный ночной прогон был неотличим от успешного.
                Environment.ExitCode = RunHeadlessClean();
                return;
            }

            // /analyze — только посчитать и напечатать, ничего не удалять.
            // Нужен, чтобы проверять правила очистки без риска что-то потерять.
            if (args != null && args.Contains("/analyze"))
            {
                RunHeadlessAnalyze();
                return;
            }

            // Признак «я единственный» — именованный мьютекс, а НЕ занятость порта.
            // Порт после аварийного завершения остаётся занятым ещё какое-то время
            // (висящие сокеты в CLOSE_WAIT/TIME_WAIT), и тогда приложение молча
            // не запускалось вообще: bind не удался, значит «уже запущено» — и выход.
            // Мьютекс освобождается ядром сразу, как процесс умер, при любом сценарии.
            bool primary;
            _instanceMutex = new Mutex(true, @"Local\SysDeck.singleinstance", out primary);
            if (!primary)
            {
                // /disk <путь> при уже открытом окне: путь уходит первому экземпляру, иначе он бы потерялся
                string forwardDisk = DiskArgument(args);
                string forwardTorrent = TorrentArgument(args);
                bool forwardDownloads = args != null && args.Contains("/downloads");
                NotifyPrimary(forwardDisk != null ? DiskMessage + forwardDisk : forwardTorrent != null ? TorrentMessage + forwardTorrent
                              : forwardDownloads ? DownloadsMessage : ShowMessage);
                return;
            }
            string startTorrent = TorrentArgument(args);

            // Канал активации — вспомогательный: не смог занять порт, работаем без него.
            TcpListener listener;
            TryBecomePrimary(out listener);

            // Необработанное исключение раньше либо молча убивало процесс (фоновый поток),
            // либо показывало стандартное окно .NET. Теперь оно попадает в crash.log рядом
            // с конфигом, а пользователь видит одно понятное сообщение.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { ReportCrash(e.Exception, false); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { ReportCrash(e.ExceptionObject as Exception, true); };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Engine engine = new Engine();
            Tr.En = engine.Config.Language == "en";
            _form = new MainForm(engine);

            // Открытие торрента из Проводника или браузера — действие пользователя: окно показывается и при «запускать свёрнутым».
            if ((startTray || engine.Config.StartMinimized) && startTorrent == null)
                _form.SetStartHidden(true);
            if (args != null && args.Contains("/selftest"))
                _form.SetSelfTest(true);
            else
            {
                SysDeck.Capture.CapLauncher.StartIfEnabled();
                SysDeck.Capture.HudLauncher.StartIfWanted();
            }
            // /disk [путь] — открыть вкладку «Диск»; с путём — сразу просканировать его
            // (удобно вызывать из Проводника или ярлыка на конкретную папку).
            if (args != null)
            {
                int di = Array.FindIndex(args, delegate(string a) { return string.Equals(a, "/disk", StringComparison.OrdinalIgnoreCase); });
                if (di >= 0) _form.SetDiskStart(DiskArgument(args));
                // /downloads — из уведомления «Загрузка перехвачена» и всплывающего окна расширения.
                if (args.Contains("/downloads")) _form.SetDownloadsStart();
                // /torrent "<файл или magnet>" — из ассоциации .torrent и протокола magnet.
                if (startTorrent != null) _form.SetTorrentStart(startTorrent);
            }

            // Слушаем сигналы "покажись" от повторных запусков
            StartActivationListener(listener);

            Application.Run(_form);
        }

        private static int _crashShowing;

        // Пишет исключение в crash.log в папке данных и показывает его. Пока одно окно
        // открыто, второе не поднимается — иначе цепочка ошибок в фоне засыпала бы экран.
        private static void ReportCrash(Exception ex, bool fatal)
        {
            string text = ex == null ? "unknown" : ex.ToString();
            try
            {
                string dir = Engine.DefaultDataDir();
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + (fatal ? " [fatal] " : " [ui] ") + text + "\r\n\r\n",
                    Encoding.UTF8);
            }
            catch { }
            if (Interlocked.CompareExchange(ref _crashShowing, 1, 0) != 0) return;
            try
            {
                string msg = Tr.S("Произошла внутренняя ошибка. Подробности записаны в crash.log в папке данных приложения.",
                                  "An internal error occurred. Details were written to crash.log in the application data folder.")
                             + "\r\n\r\n" + (ex == null ? "" : ex.GetType().Name + ": " + ex.Message);
                MessageBox.Show(msg, "SysDeck", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
            finally { Interlocked.Exchange(ref _crashShowing, 0); }
        }

        // Сухой прогон: строит категории, считает размеры и пишет отчёт в файл рядом
        // с конфигом. Ничего не удаляет — это диагностика правил и скорости обхода.
        private static void RunHeadlessAnalyze()
        {
            Engine engine = new Engine();
            Tr.En = engine.Config.Language == "en";
            Stopwatch sw = Stopwatch.StartNew();
            List<CleanCategory> cats = engine.BuildCleanCategories();
            long buildMs = sw.ElapsedMilliseconds;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("build categories: " + buildMs + " ms, categories=" + cats.Count
                          + ", winapp2 rules=" + engine.Winapp2RuleCount);
            sw.Restart();
            engine.AnalyzeCategories(cats, null);
            sb.AppendLine("analyze: " + sw.ElapsedMilliseconds + " ms");

            long total = 0; int files = 0;
            foreach (CleanCategory c in cats)
            {
                total += c.Size; files += c.FileCount;
                sb.AppendLine(Engine.FormatBytes(c.Size).PadLeft(10) + "  " + c.FileCount.ToString().PadLeft(7)
                              + "  " + (c.Recommended ? "[rec] " : "      ") + c.Title
                              + " (targets=" + c.Targets.Count + ")"
                              + (c.TargetsOff > 0 ? "  off=" + c.TargetsOff + "/" + Engine.FormatBytes(c.SizeOff) : "")
                              + (string.IsNullOrEmpty(c.Note) ? "" : "  !" + c.Note));
                // самые крупные цели — чтобы было видно, из чего складывается категория
                List<CleanTarget> top = new List<CleanTarget>(c.Targets);
                top.Sort(delegate(CleanTarget a, CleanTarget b) { return b.Size.CompareTo(a.Size); });
                for (int i = 0; i < top.Count && i < 6; i++)
                {
                    CleanTarget t = top[i];
                    if (t.Size == 0 && !t.Guarded) break;
                    sb.AppendLine("      " + Engine.FormatBytes(t.Size).PadLeft(10) + "  " + t.FileCount.ToString().PadLeft(7)
                                  + "  " + t.Path + (string.IsNullOrEmpty(t.Mask) ? "" : "  [" + t.Mask + "]")
                                  + (t.Enabled ? "" : "  [off]") + (t.Guarded ? "  [guard]" : "")
                                  + (t.Errors > 0 ? "  errors=" + t.Errors : ""));
                }
                if (c.RecycleBin) sb.AppendLine("      " + Engine.FormatBytes(c.BinSize).PadLeft(10) + "  " + c.BinCount.ToString().PadLeft(7)
                                                + "  <Recycle Bin>" + (c.BinEnabled ? "" : "  [off]"));
                if (c.Drivers != null)
                    foreach (DriverPackage d in c.Drivers)
                        sb.AppendLine("      " + Engine.FormatBytes(d.Size).PadLeft(10) + "  " + "".PadLeft(7)
                                      + "  " + d.Published + "  " + d.Original + "  " + (d.Version ?? "")
                                      + "  key=" + Engine.DriverKey(d) + (d.Enabled ? "" : "  [off]"));
            }
            // «distinct» — без двойного счёта вложенных целей (Temp внутри Temp с маской,
            // сборки Playwright внутри папки Playwright): именно эту сумму показывает окно.
            sb.AppendLine("TOTAL " + Engine.FormatBytes(Engine.DistinctSize(cats)) + "  files=" + files
                          + "  sum=" + Engine.FormatBytes(total));
            string report = sb.ToString();
            try { File.WriteAllText(Path.Combine(engine.DataDir, "analyze-report.txt"), report, Encoding.UTF8); }
            catch { }
            Console.Write(report);
        }

        // Коды возврата тихого режима. Ноль — только когда очистка действительно прошла.
        private const int AutoOk = 0;            // отработало
        private const int AutoStartFailed = 2;   // движок не поднялся: нет прав, битый конфиг
        private const int AutoFailed = 3;        // исключение во время анализа или удаления
        private const int AutoNothing = 4;       // ни одной выбранной категории — чистить нечего

        // Строка этапа уходит и в auto.log рядом с конфигом, и в stdout: задачу планировщика
        // часто запускают с перенаправлением вывода. Сбой самой записи гасим — сообщать о нём
        // всё равно некуда, а прогон из-за недоступного лога падать не должен.
        private static void AutoLog(string dir, string line)
        {
            string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [auto] " + line;
            try { Console.WriteLine(text); }
            catch { }
            if (dir == null) return;
            try
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "auto.log"), text + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        // Тихий режим: чистим только рекомендованные категории. Раньше всё тело стояло в пустом
        // catch, и процесс всегда выходил с нулём — упавший прогон выглядел как успешный.
        // Никакого UI: окно с ошибкой в расписании некому закрыть.
        // Галочки, расставленные в окне (config.json → UiChecks, «clean.cat\t<Id>\t1|0»), важнее
        // каталожного умолчания: категория, снятая пользователем, не должна чиститься по расписанию.
        private static bool HeadlessPicked(List<string> mem, CleanCategory c)
        {
            if (mem != null)
            {
                string key = "clean.cat\t" + c.Id + "\t";
                for (int i = mem.Count - 1; i >= 0; i--)
                {
                    string s = mem[i];
                    if (s != null && s.StartsWith(key, StringComparison.Ordinal)) return s.Substring(key.Length) == "1";
                }
            }
            return c.Recommended;
        }

        private static int RunHeadlessClean()
        {
            string dir = null;
            Engine engine;
            try
            {
                dir = Engine.DefaultDataDir();
                engine = new Engine();
                Tr.En = engine.Config.Language == "en";
            }
            catch (Exception ex)
            {
                AutoLog(dir, "start failed: " + ex);
                return AutoStartFailed;
            }

            AutoLog(dir, "start");
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                List<CleanCategory> cats = engine.BuildCleanCategories();
                List<CleanCategory> pick = new List<CleanCategory>();
                List<string> mem = engine.Config.UiChecks;
                foreach (CleanCategory c in cats) if (HeadlessPicked(mem, c)) pick.Add(c);
                AutoLog(dir, "categories=" + cats.Count + " picked=" + pick.Count + " (" + sw.ElapsedMilliseconds + " ms)");

                // Тихий режим никогда не показывает UAC: спрашивать некого, окна нет. Если задача
                // заведена без наивысших прав, системные категории не заваливают журнал отказами —
                // они пропускаются, и в журнале поимённо видно, какие именно и почему.
                if (!Elevation.IsElevated)
                {
                    List<CleanCategory> allowed = new List<CleanCategory>();
                    List<string> denied = new List<string>();
                    foreach (CleanCategory c in pick)
                    {
                        if (!Elevation.CategoryNeedsAdmin(c)) { allowed.Add(c); continue; }
                        // Категория делится по правам так же, как и в окне: своё чистится,
                        // системная часть остаётся до запуска с наивысшими правами.
                        List<string> keys = new List<string>();
                        CleanCategory mine = string.IsNullOrEmpty(c.Kind) ? Elevation.UserPartOf(c, keys) : null;
                        if (mine != null) allowed.Add(mine);
                        denied.Add(c.Id + (mine != null ? " (part)" : ""));
                    }
                    if (denied.Count > 0)
                        AutoLog(dir, "not elevated: left " + denied.Count + " admin-only item(s) untouched: "
                                     + string.Join(", ", denied.ToArray()));
                    pick = allowed;
                }

                if (pick.Count == 0)
                {
                    AutoLog(dir, "nothing to clean");
                    return AutoNothing;
                }

                engine.AnalyzeCategories(pick, null);
                AutoLog(dir, "analyzed " + Engine.FormatBytes(Engine.DistinctSize(pick)) + " (" + sw.ElapsedMilliseconds + " ms)");

                CleanResult res = engine.CleanCategories(pick);
                AutoLog(dir, "cleaned files=" + res.FilesDeleted + " freed=" + Engine.FormatBytes(res.Freed)
                             + " errors=" + res.Errors + " (" + sw.ElapsedMilliseconds + " ms)");
                // Занятые файлы — обычное дело и не повод объявлять прогон неудачным,
                // но их количество должно быть видно в логе.
                return AutoOk;
            }
            catch (Exception ex)
            {
                AutoLog(dir, "failed after " + sw.ElapsedMilliseconds + " ms: " + ex);
                return AutoFailed;
            }
        }

        private static bool TryBecomePrimary(out TcpListener listener)
        {
            listener = null;
            try
            {
                TcpListener l = new TcpListener(IPAddress.Loopback, SingleInstancePort);
                // позволяет занять порт, даже если от прошлого запуска остались
                // недозакрытые сокеты на нём
                l.ExclusiveAddressUse = false;
                l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                l.Start();
                listener = l;
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private const string ShowMessage = "SHOW";
        private const string DiskMessage = "DISK\n";
        private const string DownloadsMessage = "DOWNLOADS";
        private const string TorrentMessage = "TORRENT\n";
        private const int MaxActivationBytes = 256 * 1024;   // magnet-ссылка — до 64 КБ символов

        // Аргумент /torrent (или голая magnet-ссылка первым аргументом); null — нет или не торрент.
        private static string TorrentArgument(string[] args)
        {
            if (args == null || args.Length == 0) return null;
            int ti = Array.FindIndex(args, delegate(string a) { return string.Equals(a, SysDeck.Downloads.BtAssoc.OpenSwitch, StringComparison.OrdinalIgnoreCase); });
            string arg = ti >= 0 && ti + 1 < args.Length ? args[ti + 1]
                       : args[0].StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) ? args[0] : null;
            return arg == null ? null : SysDeck.Downloads.BtAssoc.ValidateOpenArgument(arg);
        }

        // Путь после /disk; null — ключа нет или путь не указан.
        private static string DiskArgument(string[] args)
        {
            if (args == null) return null;
            int di = Array.FindIndex(args, delegate(string a) { return string.Equals(a, "/disk", StringComparison.OrdinalIgnoreCase); });
            if (di < 0 || di + 1 >= args.Length || args[di + 1].StartsWith("/")) return null;
            return args[di + 1];
        }

        private static void NotifyPrimary(string message)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    // Connect без таймаута может висеть десятки секунд; нам нужен
                    // быстрый отказ — окно всё равно покажет уже запущенный экземпляр.
                    IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, SingleInstancePort, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(1500)) return;
                    c.EndConnect(ar);
                    byte[] msg = Encoding.UTF8.GetBytes(message);
                    c.GetStream().Write(msg, 0, msg.Length);
                }
            }
            catch { }
        }

        private static void StartActivationListener(TcpListener listener)
        {
            if (listener == null) return;
            Thread t = new Thread(delegate()
            {
                while (true)
                {
                    TcpClient client;
                    // Ошибка самого listener'а — выходим; ошибка на одном соединении
                    // не должна навсегда лишать приложение канала активации.
                    try { client = listener.AcceptTcpClient(); }
                    catch { break; }

                    // using обязателен: раньше при исключении в Read соединение
                    // оставалось незакрытым и висело в CLOSE_WAIT до конца работы.
                    string message = "";
                    using (client)
                    {
                        try
                        {
                            // Сообщение короткое («SHOW», «DISK\n<путь>», «TORRENT\n<файл или magnet>»), отправитель закрывает соединение сразу.
                            client.ReceiveTimeout = 1000;
                            byte[] buf = new byte[MaxActivationBytes];
                            int total = 0, read;
                            NetworkStream stream = client.GetStream();
                            while (total < buf.Length && (read = stream.Read(buf, total, buf.Length - total)) > 0) total += read;
                            message = Encoding.UTF8.GetString(buf, 0, total);
                        }
                        catch { }
                    }
                    string diskPath = message.StartsWith(DiskMessage, StringComparison.Ordinal) ? message.Substring(DiskMessage.Length) : null;
                    string torrent = message.StartsWith(TorrentMessage, StringComparison.Ordinal) ? message.Substring(TorrentMessage.Length) : null;
                    try
                    {
                        if (_form != null && !_form.IsDisposed && _form.IsHandleCreated)
                            _form.BeginInvoke((MethodInvoker)delegate
                            {
                                if (message == DownloadsMessage) _form.OpenDownloads();
                                else if (torrent != null) _form.OpenTorrent(torrent);
                                else if (string.IsNullOrEmpty(diskPath)) _form.ShowWindow();
                                else _form.OpenDiskScan(diskPath);
                            });
                    }
                    catch { }
                }
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
