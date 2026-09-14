// Windows Process Cleaner — «Загрузки»: стыковка модуля видео. Здесь и только здесь ядро склейки (Media.*.cs),
// движок потоков (Downloads.Hls/Dash/MediaFetch/Media.cs) и инструменты (Downloads.Ytdlp*.cs) соединяются с очередью.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Очередь, мост и окно знают о видео только через MdHooks: пока эти точки пусты, видео-загрузки недоступны, а всё
// остальное работает как раньше. Установку yt-dlp и Deno делает кнопка в настройках — этот файл ничего не скачивает.
using System;
using System.Threading;

namespace WindowsProcessCleaner.Downloads
{
    internal static class MdWiring
    {
        private static readonly object Lock = new object();
        private static bool _installed;
        private static bool _toolsChecking;
        private static DateTime _toolsNextRun = DateTime.MinValue;

        // Вызывается один раз при старте процесса загрузок (и из тестов стыковки). Повторные вызовы ничего не делают.
        public static void Install()
        {
            lock (Lock)
            {
                if (_installed) return;
                _installed = true;
                MdHooks.CreateRun = CreateRun;
                MdHooks.Mux = MdMux.Run;
                MdHooks.Extract = MdYtdlp.Extract;
                MdHooks.DeleteParts = DlMediaRun.DeleteParts;
            }
        }

        private static IDlRun CreateRun(DlItem item, DlSettings s, DlTokenBucket global, IDlEnvironment env, IDlTransferHost host)
        {
            return new DlMediaRun(item, s, global, env, host);
        }

        // Видео-загрузку можно создать всегда; yt-dlp нужен только страницам (MdSource.Ytdlp).
        public static bool PageSupportAvailable { get { return MdYtdlp.Available; } }

        // Часы между попытками фоновой проверки. Сама MdTools.CheckUpdate ходит к GitHub не чаще раза в сутки
        // (отметка в tools.json), здесь только дешёвая защита от частых попыток внутри одного процесса.
        internal static int ToolsCheckHours = 6;

        // Зовётся из Tick очереди. Ничего не скачивает: только узнаёт, вышла ли новая версия, чтобы настройки показали
        // «Есть обновление». Пока инструменты не установлены, проверять нечего — обновлять нечего.
        internal static int ToolsTicks;          // только для тестов: дошёл ли сюда Tick очереди и с каким флажком
        internal static bool ToolsTickEnabled;

        public static void ToolsTick(bool enabled, DateTime now)
        {
            lock (Lock)
            {
                ToolsTicks++;
                ToolsTickEnabled = enabled;
                if (!enabled || _toolsChecking || now < _toolsNextRun) return;
                if (!MdTools.YtdlpReady && !MdTools.DenoReady) return;
                _toolsNextRun = now.AddHours(ToolsCheckHours);
                _toolsChecking = true;
            }
            Thread t = new Thread(ToolsCheckRun);
            t.IsBackground = true;
            t.Name = "wpc-dl-tools-check";
            t.Start();
        }

        private static void ToolsCheckRun()
        {
            try
            {
                bool available;
                string error;
                MdTools.CheckUpdate(false, null, out available, out error);
            }
            catch (Exception ex) { DlLog.Report(ex); }
            finally { lock (Lock) _toolsChecking = false; }
        }
    }
}
