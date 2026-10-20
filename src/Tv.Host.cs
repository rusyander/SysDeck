// SysDeck — «ТВ-трансляция»: один сервер на процесс, его настройки и действия окна.
// SysDeck держит сервер, пока работает в трее; отдельное приложение PcStream — так же, своим процессом.
// Общее ядро с отдельным приложением PcStream (см. Tv.Model.cs).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace SysDeck.TvStream
{
    internal static class TvHost
    {
        private static readonly object Lock = new object();
        private static string _dataDir;
        private static TvSettings _settings;
        private static TvFfmpeg _ffmpeg;
        private static TvServer _server;
        public static string LastError;

        // Любое изменение состояния (старт, стоп, новый зритель, папки) — окно перерисовывается.
        public static event EventHandler Changed;

        public static void Init(string dataDir)
        {
            lock (Lock)
            {
                if (_server != null) return;
                _dataDir = dataDir;
                TvFfmpeg.OwnDir = Path.Combine(dataDir, "ffmpeg");
                _settings = TvSettings.Load(SettingsFile);
                _ffmpeg = TvFfmpeg.Locate();
                _server = new TvServer();
                _server.Settings = delegate { lock (Lock) return _settings.Clone(); };
                _server.Ffmpeg = delegate { return _ffmpeg; };
                _server.ThumbDir = Path.Combine(dataDir, "thumbs");
                _server.Resource = Resource;
                _server.Changed += delegate { Fire(); };
            }
        }

        public static bool Ready { get { return _server != null; } }
        public static string SettingsFile { get { return Path.Combine(_dataDir, "tvstream.json"); } }
        public static TvServer Server { get { return _server; } }
        public static TvFfmpeg Ffmpeg { get { return _ffmpeg; } }
        public static bool Running { get { return _server != null && _server.Running; } }

        // Адрес для ввода в телевизоре (без http://): так его ждёт Start Parameter в Media Station X.
        public static string Address
        {
            get { return TvNet.LanAddress() + ":" + (Running ? _server.Port : Snapshot().Port); }
        }

        public static TvSettings Snapshot() { lock (Lock) return _settings.Clone(); }

        // Окно меняет копию и отдаёт её сюда: запись на диск + уведомление. Смена порта на работающем сервере
        // перезапускает его — иначе телевизор стучался бы по старому адресу, а окно показывало новый.
        public static void Apply(TvSettings next)
        {
            int oldPort;
            lock (Lock)
            {
                oldPort = _settings.Port;
                next.Normalize();
                _settings = next.Clone();
                try { _settings.Save(SettingsFile); LastError = null; }
                catch (Exception ex) { LastError = SysDeck.Tr.S("Не удалось сохранить настройки: ", "Could not save settings: ") + ex.Message; }
            }
            if (Running && oldPort != next.Port) { Stop(); Start(); }
            Fire();
        }

        public static bool Start()
        {
            string error;
            bool ok = _server.Start(Snapshot().Port, out error);
            LastError = ok ? null : error;
            Fire();
            return ok;
        }

        public static void Stop() { _server.Stop(); Fire(); }

        // При запуске приложения: включаем, только если так решил пользователь и есть что показывать.
        public static void AutoStartIfWanted()
        {
            TvSettings s = Snapshot();
            if (s.AutoStart && s.Folders.Count > 0 && !Running) Start();
        }

        public static void Shutdown() { if (_server != null) _server.Stop(); }

        public static void RelocateFfmpeg() { _ffmpeg = TvFfmpeg.Locate(); Fire(); }

        private static void Fire()
        {
            EventHandler h = Changed;
            if (h != null) { try { h(null, EventArgs.Empty); } catch (Exception) { } }
        }

        // Встроенные файлы: веб-интерфейс телевизора и инструкция (ресурсы tvstream/*, см. resources.bat).
        public static byte[] Resource(string name)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (s == null) return null;
                    MemoryStream ms = new MemoryStream();
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch (Exception) { return null; }
        }

        // Инструкция открывается системным просмотрщиком PDF: копия во временной папке, исходник внутри exe.
        public static bool OpenGuide()
        {
            byte[] pdf = Resource("tvstream/guide.pdf");
            if (pdf == null) return false;
            try
            {
                string file = Path.Combine(Path.GetTempPath(), "PC Stream — " + SysDeck.Tr.S("инструкция", "guide") + ".pdf");
                File.WriteAllBytes(file, pdf);
                Process.Start(file);
                return true;
            }
            catch (Exception) { return false; }
        }

        public static void OpenInBrowser() { try { Process.Start("http://localhost:" + (Running ? _server.Port : Snapshot().Port) + "/"); } catch (Exception) { } }

        public static string ExePath
        {
            get
            {
                try { Assembly a = Assembly.GetEntryAssembly(); return a != null ? a.Location : Process.GetCurrentProcess().MainModule.FileName; }
                catch (Exception) { return ""; }
            }
        }

        // Проверка брандмауэра идёт в фоне (PowerShell ~1 с); результат — через Changed.
        private static volatile TvFirewallState _firewall;
        private static int _fwChecking;
        public static TvFirewallState Firewall { get { return _firewall; } }

        public static void CheckFirewallAsync()
        {
            if (System.Threading.Interlocked.Exchange(ref _fwChecking, 1) == 1) return;
            int port = Snapshot().Port;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { _firewall = TvFirewall.Check(ExePath, port, TvNet.LanAddress()); }
                finally { System.Threading.Interlocked.Exchange(ref _fwChecking, 0); }
                Fire();
            });
        }

        // Одно окно UAC: снять запреты для программы и открыть порт своей подсети. Само окно прав не поднимает.
        public static bool FixFirewall()
        {
            bool ok = TvFirewall.Fix(ExePath, Snapshot().Port);
            CheckFirewallAsync();
            return ok;
        }

        // ffmpeg — скачивание по кнопке в папку данных (Tv.Setup.cs). Прогресс и итог — через Changed.
        public static bool FfmpegBusy { get { return _ffBusy; } }
        public static long FfmpegGot { get { return System.Threading.Interlocked.Read(ref _ffGot); } }
        public static long FfmpegTotal { get { return System.Threading.Interlocked.Read(ref _ffTotal); } }
        public static string FfmpegError;
        private static volatile bool _ffBusy, _ffCancel;
        private static long _ffGot, _ffTotal;
        private static DateTime _ffFired;

        public static string FfmpegDir { get { return Path.Combine(_dataDir, "ffmpeg"); } }

        public static void DownloadFfmpeg()
        {
            lock (Lock)
            {
                if (_ffBusy) return;
                _ffBusy = true;
                _ffCancel = false;
                FfmpegError = null;
                _ffGot = 0;
                _ffTotal = TvFfmpegSetup.ApproxBytes;
            }
            Fire();
            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                string error;
                bool ok = TvFfmpegSetup.Install(FfmpegDir, delegate(long got, long total)
                {
                    System.Threading.Interlocked.Exchange(ref _ffGot, got);
                    System.Threading.Interlocked.Exchange(ref _ffTotal, total);
                    if ((DateTime.Now - _ffFired).TotalMilliseconds > 300) { _ffFired = DateTime.Now; Fire(); }
                }, delegate { return _ffCancel; }, out error);
                FfmpegError = ok || _ffCancel ? null : error;
                _ffmpeg = TvFfmpeg.Locate();
                _ffBusy = false;
                Fire();
            });
            t.IsBackground = true;
            t.Name = "tv-ffmpeg-setup";
            t.Start();
        }

        public static void CancelFfmpegDownload() { _ffCancel = true; }
    }
}
