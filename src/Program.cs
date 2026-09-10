// Windows Process Cleaner
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
//  - История очисток и настройки в JSON (%APPDATA%\WindowsProcessCleaner); запись
//    атомарная (tmp + Replace), битый config.json откладывается как .corrupt.
//  - Single-instance: именованный Mutex; повторный запуск показывает окно первого
//    экземпляра через локальный TCP-порт 49876.
//  - Ключи: /tray (свернуть в трей), /auto (тихая очистка диска), /analyze (только отчёт),
//    /disk [путь] (открыть вкладку «Диск» и сразу просканировать путь).

// Windows Process Cleaner — точка входа, локализация, single-instance
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
            _instanceMutex = new Mutex(true, @"Local\WindowsProcessCleaner.singleinstance", out primary);
            if (!primary)
            {
                NotifyPrimaryShow();
                return;
            }

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

            if (startTray || engine.Config.StartMinimized)
                _form.SetStartHidden(true);
            if (args != null && args.Contains("/selftest"))
                _form.SetSelfTest(true);
            // /disk [путь] — открыть вкладку «Диск»; с путём — сразу просканировать его
            // (удобно вызывать из Проводника или ярлыка на конкретную папку).
            if (args != null)
            {
                int di = Array.FindIndex(args, delegate(string a) { return string.Equals(a, "/disk", StringComparison.OrdinalIgnoreCase); });
                if (di >= 0)
                {
                    string dp = di + 1 < args.Length && !args[di + 1].StartsWith("/") ? args[di + 1] : null;
                    _form.SetDiskStart(dp);
                }
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
                MessageBox.Show(msg, "Windows Process Cleaner", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        private const int AutoNothing = 4;       // ни одной рекомендованной категории — чистить нечего

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
                foreach (CleanCategory c in cats) if (c.Recommended) pick.Add(c);
                AutoLog(dir, "categories=" + cats.Count + " recommended=" + pick.Count + " (" + sw.ElapsedMilliseconds + " ms)");
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

        private static void NotifyPrimaryShow()
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
                    byte[] msg = Encoding.ASCII.GetBytes("SHOW");
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
                    using (client)
                    {
                        try
                        {
                            byte[] buf = new byte[16];
                            client.ReceiveTimeout = 1000;
                            client.GetStream().Read(buf, 0, buf.Length);
                        }
                        catch { }
                    }
                    try
                    {
                        if (_form != null && !_form.IsDisposed && _form.IsHandleCreated)
                            _form.BeginInvoke((MethodInvoker)delegate { _form.ShowWindow(); });
                    }
                    catch { }
                }
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
