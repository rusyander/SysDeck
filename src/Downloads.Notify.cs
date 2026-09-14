// Windows Process Cleaner — «Загрузки»: уведомления фонового процесса «готово» и «не удалось».
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Окно уведомления то же, что у «Захвата» (без активации, поверх всех, стопкой в углу). У процесса загрузок нет цикла
// сообщений — уведомления живут в своём STA-потоке. Файл по щелчку не открывается никогда: только «Показать в папке».
using System;
using System.Threading;
using System.Windows.Forms;
using WindowsProcessCleaner.Capture;

namespace WindowsProcessCleaner.Downloads
{
    internal sealed class DlNotifier : IDisposable
    {
        // Сколько процесс ещё считается занятым после показа: уведомление висит 6–8 с и замирает под курсором.
        private const int BusySeconds = 30;

        private readonly Thread _thread;
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);
        private Control _invoker;
        private ToastHost _host;
        private long _busyUntilTicks;
        private int _holds;

        public DlNotifier()
        {
            _thread = new Thread(Run);
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Name = "wpc-dl-notify";
            _thread.Start();
            _ready.WaitOne(5000);
        }

        // Пока открыт вопрос «куда скачивать», процесс занят сколько угодно долго: иначе он ушёл бы из-под открытого окна.
        public bool Busy { get { return Interlocked.CompareExchange(ref _holds, 0, 0) > 0 || DateTime.UtcNow.Ticks < Interlocked.Read(ref _busyUntilTicks); } }

        public void Hold(bool on) { Interlocked.Add(ref _holds, on ? 1 : -1); }

        // Пока открыт вопрос «куда скачивать», уведомления ждут в очереди: они перекрывают окно и путают ответ.
        // Вызывается из того же потока (Downloads.FolderPrompt.cs), поэтому к ToastHost обращаемся напрямую.
        public void HoldToasts(bool on)
        {
            ToastHost host = _host;
            if (host == null) return;
            if (on) host.Hold(); else host.Release();
        }

        private void Run()
        {
            try
            {
                CapNative.UsePerMonitorDpiOnThisThread();
                Control invoker = new Control();
                IntPtr handle = invoker.Handle;     // окно-получатель создаётся в этом потоке
                if (handle == IntPtr.Zero) return;
                _host = new ToastHost();
                _invoker = invoker;
                _ready.Set();
                Application.Run();
            }
            catch (Exception ex) { DlLog.Report(ex); }
            finally
            {
                _ready.Set();
                if (_host != null) _host.Dispose();
                if (_invoker != null) _invoker.Dispose();
            }
        }

        // Чужая работа в этом же потоке: окно вопроса «куда скачивать» (Downloads.FolderPrompt.cs). false — потока нет.
        public bool Post(MethodInvoker work)
        {
            Control invoker = _invoker;
            if (work == null || invoker == null) return false;
            Interlocked.Exchange(ref _busyUntilTicks, DateTime.UtcNow.AddSeconds(BusySeconds).Ticks);
            try { invoker.BeginInvoke(work); return true; }
            catch (InvalidOperationException) { return false; }
        }

        public void Show(DlNotice n)
        {
            Control invoker = _invoker;
            if (n == null || invoker == null) return;
            Interlocked.Exchange(ref _busyUntilTicks, DateTime.UtcNow.AddSeconds(BusySeconds).Ticks);
            ToastInfo info = InfoFor(n);
            try { invoker.BeginInvoke((MethodInvoker)delegate { _host.Show(info, null); }); }
            catch (InvalidOperationException) { }
        }

        internal static ToastInfo InfoFor(DlNotice n)
        {
            switch (n.Kind)
            {
                case DlNoticeKind.Completed:
                    return ToastInfo.Download(Tr.S("Загрузка завершена", "Download complete"), n.Name, n.Path);
                case DlNoticeKind.Intercepted:
                    return ToastInfo.Intercepted(Tr.S("Загрузка перехвачена", "Download taken over"), n.Name,
                                                 Tr.S("Открыть загрузки", "Open downloads"), n.OpenApp,
                                                 Tr.S("Отдать браузеру", "Give back to browser"), n.GiveBack);
                case DlNoticeKind.UpdateAvailable:
                    return ToastInfo.Intercepted(Tr.S("Новая версия раздачи: ", "New version of the torrent: ") + n.Name, n.Text,
                                                 Tr.S("Открыть загрузки", "Open downloads"), n.OpenApp, null, null);
                case DlNoticeKind.NeedsLink:
                    return ToastInfo.Error(Tr.S("Нужна новая ссылка: ", "A fresh link is needed: ") + n.Name,
                                           Tr.S("Скачанное сохранено. Откройте «Загрузки» и обновите ссылку.", "What was downloaded is kept. Open Downloads and refresh the link."));
                default:
                    return ToastInfo.Error(Tr.S("Загрузка не удалась: ", "Download failed: ") + n.Name, n.Text);
            }
        }

        public void Dispose()
        {
            Control invoker = _invoker;
            if (invoker != null)
            {
                try { invoker.BeginInvoke((MethodInvoker)delegate { Application.ExitThread(); }); }
                catch (InvalidOperationException) { }
            }
            _thread.Join(3000);
            _ready.Close();
        }
    }
}
