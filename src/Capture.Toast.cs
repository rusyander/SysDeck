// SysDeck — «Захват»: уведомления о снимке в углу монитора.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Системные уведомления Windows требуют у неупакованной программы AppUserModelID и ярлыка в «Пуске», поэтому окно своё:
// без активации (игра не теряет фокус), поверх всех, не попадает на снимки (WDA_EXCLUDEFROMCAPTURE, Windows 10 2004+;
// на более старых сборках окна прячутся на время снимка).
using System;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SysDeck.Capture
{
    internal enum ToastKind { Copied, Saved, Error, Download, Intercepted }

    internal sealed class ToastInfo
    {
        public ToastKind Kind;
        public Bitmap Thumb;        // переходит во владение уведомления
        public string Path;
        public string Title;
        public string Text;
        // Текстовые кнопки «Загрузок» (Intercepted): действие выполняется вне потока окна уведомления.
        public string PrimaryText, SecondaryText;
        public Action Primary, Secondary;

        public static ToastInfo Copied(Bitmap thumb)
        {
            ToastInfo t = new ToastInfo();
            t.Kind = ToastKind.Copied;
            t.Thumb = thumb;
            t.Title = Tr.S("Скопировано в буфер обмена", "Copied to clipboard");
            t.Text = Tr.S("Снимок не сохранён в файл · Ctrl+V — вставить", "Not saved to a file · Ctrl+V to paste");
            return t;
        }

        public static ToastInfo Saved(Bitmap thumb, string path, bool copied)
        {
            ToastInfo t = new ToastInfo();
            t.Kind = ToastKind.Saved;
            t.Thumb = thumb;
            t.Path = path;
            t.Title = copied ? Tr.S("Снимок сохранён и скопирован", "Screenshot saved and copied")
                             : Tr.S("Снимок сохранён", "Screenshot saved");
            t.Text = System.IO.Path.GetFileName(path);
            return t;
        }

        // «Видео сохранено · 02:14 · 186 МБ»; note — предупреждение записи (без микрофона, программный кодировщик…).
        public static ToastInfo VideoSaved(Bitmap thumb, string path, TimeSpan duration, long bytes, string note)
        {
            ToastInfo t = new ToastInfo();
            t.Kind = ToastKind.Saved;
            t.Thumb = thumb;
            t.Path = path;
            t.Title = Tr.S("Видео сохранено", "Video saved") + " · " + RecordPlan.FormatElapsed(duration) + " · " + RecordPlan.FormatBytes(bytes);
            t.Text = string.IsNullOrEmpty(note) ? System.IO.Path.GetFileName(path) : note;
            return t;
        }

        // Загрузка завершена (процесс «Загрузок»): только «Показать в папке», щелчок по уведомлению файл не открывает.
        public static ToastInfo Download(string title, string text, string path)
        {
            ToastInfo t = new ToastInfo();
            t.Kind = ToastKind.Download;
            t.Title = title;
            t.Text = text ?? "";
            t.Path = path;
            return t;
        }

        // Загрузку забрала программа у браузера: «Открыть загрузки» и «Отдать браузеру».
        public static ToastInfo Intercepted(string title, string text, string primaryText, Action primary, string secondaryText, Action secondary)
        {
            ToastInfo t = new ToastInfo();
            t.Kind = ToastKind.Intercepted;
            t.Title = title;
            t.Text = text ?? "";
            t.PrimaryText = primaryText;
            t.Primary = primary;
            t.SecondaryText = secondaryText;
            t.Secondary = secondary;
            return t;
        }

        public static ToastInfo Error(string title, string text)
        {
            ToastInfo t = new ToastInfo();
            t.Kind = ToastKind.Error;
            t.Title = title;
            t.Text = text ?? "";
            return t;
        }
    }

    internal static class CapFonts
    {
        private static int _iconsState;     // 0 — не проверяли, 1 — есть, 2 — нет

        // Segoe MDL2 Assets есть в Windows 10 и 11 (Segoe Fluent Icons — только в 11). Если шрифта нет, GDI+ молча
        // подставит Microsoft Sans Serif и вместо значков будут квадраты — тогда кнопки подписываются текстом.
        public static bool HasIcons
        {
            get
            {
                if (_iconsState == 0)
                {
                    try
                    {
                        using (Font f = new Font("Segoe MDL2 Assets", 10f))
                            _iconsState = string.Equals(f.Name, "Segoe MDL2 Assets", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                    }
                    catch { _iconsState = 2; }
                }
                return _iconsState == 1;
            }
        }

        public static Font Icons(float pixels)
        {
            return new Font(HasIcons ? "Segoe MDL2 Assets" : "Segoe UI", HasIcons ? pixels : pixels * 0.7f, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        public static string Glyph(string icon, string fallback) { return HasIcons ? icon : fallback; }

        public const string Save = "\uE74E", Copy = "\uE8C8", Folder = "\uE838", Delete = "\uE74D", Edit = "\uE70F",
                            Close = "\uE711", Record = "\uE7C8", Undo = "\uE7A7", Error = "\uE783",
                            Play = "\uE768", Pause = "\uE769", Stop = "\uE71A", Done = "\uE73E";
    }

    internal sealed class ToastHost : IDisposable
    {
        private const int MaxPerMonitor = 3;

        private readonly List<ToastWindow> _open = new List<ToastWindow>();
        private readonly List<KeyValuePair<ToastInfo, MonitorInfo>> _queue = new List<KeyValuePair<ToastInfo, MonitorInfo>>();
        private readonly System.Windows.Forms.Timer _poll = new System.Windows.Forms.Timer();
        private bool _enabled = true, _defer = true;
        private int _seconds = 6, _holds;

        public event Action<string, MonitorInfo> EditRequested;
        public event Action<string> GalleryRequested;

        public ToastHost()
        {
            _poll.Interval = 2000;
            _poll.Tick += delegate { Flush(); };
        }

        public void Apply(CapSettings s)
        {
            _enabled = s.ToastEnabled;
            _seconds = s.ToastSeconds;
            _defer = s.DeferToastsInFullscreen;
        }

        public void Show(ToastInfo info, MonitorInfo mon)
        {
            // Выключенные уведомления не отменяют ошибок: иначе несохранённый снимок пропал бы молча.
            if (!_enabled && info.Kind != ToastKind.Error) { DisposeInfo(info); return; }
            if (_holds > 0 || (_defer && FullscreenBusy()))
            {
                _queue.Add(new KeyValuePair<ToastInfo, MonitorInfo>(info, mon));
                while (_queue.Count > MaxPerMonitor) { DisposeInfo(_queue[0].Key); _queue.RemoveAt(0); }
                if (_holds == 0) _poll.Start();
                return;
            }
            Present(info, mon);
        }

        // Пока открыто выделение области, новые уведомления ждут: окно поверх выделения перехватывало бы щелчки.
        public void Hold() { _holds++; }

        public void Release()
        {
            if (_holds > 0) _holds--;
            if (_holds == 0) Flush();
        }

        // Спрятать уведомления, которые иначе попадут на снимок. Возвращает true, если что-то спрятали (нужна пауза
        // на кадр композиции, прежде чем снимать).
        public bool HideForCapture()
        {
            bool hid = false;
            foreach (ToastWindow w in _open)
                if (w.Visible && !w.ExcludedFromCapture) { w.Visible = false; hid = true; }
            return hid;
        }

        public void RestoreAfterCapture()
        {
            foreach (ToastWindow w in _open) if (!w.Visible && !w.IsDisposed) w.ShowInactive();
        }

        private static bool FullscreenBusy()
        {
            try
            {
                int state;
                if (CapNative.SHQueryUserNotificationState(out state) != 0) return false;
                return state == CapNative.QUNS_BUSY || state == CapNative.QUNS_RUNNING_D3D_FULL_SCREEN || state == CapNative.QUNS_PRESENTATION_MODE;
            }
            catch { return false; }
        }

        private void Flush()
        {
            if (_holds > 0) { _poll.Stop(); return; }
            if (_queue.Count == 0) { _poll.Stop(); return; }
            if (_defer && FullscreenBusy()) { _poll.Start(); return; }
            _poll.Stop();
            List<KeyValuePair<ToastInfo, MonitorInfo>> items = new List<KeyValuePair<ToastInfo, MonitorInfo>>(_queue);
            _queue.Clear();
            foreach (KeyValuePair<ToastInfo, MonitorInfo> kv in items) Present(kv.Key, kv.Value);
        }

        private void Present(ToastInfo info, MonitorInfo mon)
        {
            if (mon == null)
            {
                List<MonitorInfo> all = ScreenGrab.Monitors();
                foreach (MonitorInfo m in all) if (m.Primary) mon = m;
                if (mon == null && all.Count > 0) mon = all[0];
            }
            if (mon == null) { DisposeInfo(info); return; }
            List<ToastWindow> same = OnMonitor(mon.Device);
            while (same.Count >= MaxPerMonitor) { same[0].CloseToast(); same.RemoveAt(0); }

            ToastWindow w = new ToastWindow(info, mon, info.Kind == ToastKind.Error ? Math.Max(_seconds, 8) : _seconds);
            w.EditRequested += delegate(string path, MonitorInfo m) { if (EditRequested != null) EditRequested(path, m); };
            w.GalleryRequested += delegate(string path) { if (GalleryRequested != null) GalleryRequested(path); };
            w.ClosedToast += delegate
            {
                _open.Remove(w);
                Layout(w.Monitor.Device);
                w.Dispose();
            };
            _open.Add(w);
            Layout(mon.Device);
            w.ShowInactive();
        }

        private List<ToastWindow> OnMonitor(string device)
        {
            List<ToastWindow> list = new List<ToastWindow>();
            foreach (ToastWindow w in _open) if (!w.IsClosingToast && w.Monitor.Device == device) list.Add(w);
            return list;
        }

        // Стопка растёт вверх от правого нижнего угла рабочей области: новое уведомление — внизу.
        private void Layout(string device)
        {
            List<ToastWindow> list = OnMonitor(device);
            if (list.Count == 0) return;
            float s = list[0].DpiScale;
            Rectangle work = list[0].Monitor.WorkArea;
            int margin = (int)(12 * s), gap = (int)(8 * s);
            int bottom = work.Bottom - margin;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                ToastWindow w = list[i];
                w.Location = new Point(work.Right - margin - w.Width, bottom - w.Height);
                bottom -= w.Height + gap;
            }
        }

        private static void DisposeInfo(ToastInfo info)
        {
            if (info != null && info.Thumb != null) { info.Thumb.Dispose(); info.Thumb = null; }
        }

        // Миниатюра с запасом на масштаб 200 %: окно рисует её в 160x90 логических точек.
        public static Bitmap MakeThumbnail(Bitmap source)
        {
            const int maxW = 320, maxH = 180;
            double k = Math.Min(1.0, Math.Min(maxW / (double)source.Width, maxH / (double)source.Height));
            int w = Math.Max(1, (int)Math.Round(source.Width * k)), h = Math.Max(1, (int)Math.Round(source.Height * k));
            Bitmap thumb = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(source, new Rectangle(0, 0, w, h));
            }
            return thumb;
        }

        // Открыть файл программой по умолчанию (своё окно просмотра появится вместе с галереей).
        public static void OpenFile(string path)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(path);
                psi.UseShellExecute = true;
                using (Process.Start(psi)) { }
            }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        public static void ShowInFolder(string path)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"");
                psi.UseShellExecute = false;
                using (Process.Start(psi)) { }
            }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        // Папка целиком — в Проводнике; нет её ещё — создаётся, чтобы кнопка не молчала.
        public static void OpenFolder(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "\"" + dir + "\"");
                psi.UseShellExecute = false;
                using (Process.Start(psi)) { }
            }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        public void Dispose()
        {
            _poll.Dispose();
            foreach (KeyValuePair<ToastInfo, MonitorInfo> kv in _queue) DisposeInfo(kv.Key);
            _queue.Clear();
            foreach (ToastWindow w in new List<ToastWindow>(_open)) w.Dispose();     // отложенное удаление выполнит Dispose окна
            _open.Clear();
        }
    }

    internal sealed class ToastWindow : Form
    {
        private enum Btn { None, Copy, Folder, Delete, Undo, Edit, Close, Primary, Secondary, Gallery }

        private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int cmd);

        public event EventHandler ClosedToast;
        public event Action<string, MonitorInfo> EditRequested;
        public event Action<string> GalleryRequested;       // «Галерея»: путь сохранённого файла, папку выбирает агент

        private readonly ToastInfo _info;
        private readonly MonitorInfo _monitor;
        private readonly float _scale;
        private readonly int _lifeMs;
        private readonly Font _titleFont, _textFont, _iconFont;
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private readonly List<KeyValuePair<Btn, Rectangle>> _buttons = new List<KeyValuePair<Btn, Rectangle>>();
        private readonly ToolTip _tip = new ToolTip();
        private int _leftMs;
        private Btn _hot = Btn.None;
        private bool _pendingDelete, _closing;
        private string _status;

        public ToastWindow(ToastInfo info, MonitorInfo monitor, int seconds)
        {
            _info = info;
            _monitor = monitor;
            _scale = Math.Max(1f, CapDpi.ScaleAt(new Point(monitor.WorkArea.X + monitor.WorkArea.Width / 2, monitor.WorkArea.Y + monitor.WorkArea.Height / 2)));
            _lifeMs = Math.Max(1, seconds) * 1000;
            _leftMs = _lifeMs;
            _titleFont = new Font("Segoe UI Semibold", 14f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _textFont = new Font("Segoe UI", 12.5f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _iconFont = CapFonts.Icons(15f * _scale);

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(32, 32, 32);
            Text = "SysDeck — capture";
            int height = info.Kind == ToastKind.Error ? S(92) : S(114);
            // Ширина вмещает подписанную «Галерею» и четыре значка справа от миниатюры.
            Size = new Size(S(460), height);
            // Окно создаётся сразу на своём мониторе — тогда DPI у него правильный с первого сообщения.
            Location = new Point(monitor.WorkArea.Right - Width - S(12), monitor.WorkArea.Bottom - Height - S(12));
            _tip.ShowAlways = true;

            _timer.Interval = 100;
            _timer.Tick += OnTick;
            LayoutButtons();
        }

        public MonitorInfo Monitor { get { return _monitor; } }
        public float DpiScale { get { return _scale; } }
        public bool IsClosingToast { get { return _closing; } }
        public bool ExcludedFromCapture { get; private set; }

        private int S(float v) { return (int)Math.Round(v * _scale); }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= CapNative.WS_EX_TOOLWINDOW | CapNative.WS_EX_NOACTIVATE | CapNative.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { ExcludedFromCapture = CapNative.SetWindowDisplayAffinity(Handle, CapNative.WDA_EXCLUDEFROMCAPTURE); }
            catch (EntryPointNotFoundException) { ExcludedFromCapture = false; }
            try
            {
                int round = DWMWCP_ROUND;
                DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);   // Windows 10 просто откажет
            }
            catch (DllNotFoundException) { }
        }

        public void ShowInactive()
        {
            if (!IsHandleCreated) CreateHandle();
            ShowWindow(Handle, 4);  // SW_SHOWNOACTIVATE
            Visible = true;
            _timer.Start();
        }

        protected override void WndProc(ref Message m)
        {
            // Щелчок по уведомлению не забирает фокус у игры или окна, где сделан снимок.
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = new IntPtr(MA_NOACTIVATE); return; }
            base.WndProc(ref m);
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (Bounds.Contains(Control.MousePosition)) return;     // под курсором время стоит
            _leftMs -= _timer.Interval;
            if (_leftMs <= 0) CloseToast();
            else Invalidate(new Rectangle(0, Height - S(2) - 2, Width, S(2) + 2));
        }

        public void CloseToast()
        {
            if (_closing) return;
            _closing = true;
            _timer.Stop();
            if (_pendingDelete) Recycle();
            Hide();
            // После выхода из цикла сообщений (агент завершается) дескриптора уже нет — и сообщать некому.
            EventHandler h = ClosedToast;
            if (h != null && IsHandleCreated && !IsDisposed) BeginInvoke(h, this, EventArgs.Empty);
        }

        // ---------- кнопки ----------

        private void LayoutButtons()
        {
            _buttons.Clear();
            int size = S(30), gap = S(4);
            _buttons.Add(new KeyValuePair<Btn, Rectangle>(Btn.Close, new Rectangle(Width - S(8) - size, S(8), size, size)));
            if (_info.Kind != ToastKind.Saved && _info.Kind != ToastKind.Download && _info.Kind != ToastKind.Intercepted) return;
            List<Btn> row = new List<Btn>();
            if (_info.Kind == ToastKind.Intercepted)
            {
                if (_info.Primary != null) row.Add(Btn.Primary);
                if (_info.Secondary != null) row.Add(Btn.Secondary);
            }
            else if (_info.Kind == ToastKind.Download) { if (!string.IsNullOrEmpty(_info.Path)) row.Add(Btn.Folder); }
            else if (_pendingDelete) row.Add(Btn.Undo);
            else
            {
                row.Add(Btn.Gallery);
                if (CapFeatures.Editor && !ImageStore.IsVideo(_info.Path)) row.Add(Btn.Edit);
                row.Add(Btn.Copy);
                row.Add(Btn.Folder);
                row.Add(Btn.Delete);
            }
            int x = TextLeft;
            int y = Height - S(10) - size;
            foreach (Btn b in row)
            {
                string caption = CaptionOf(b);
                int w = b == Btn.Undo ? S(110) : caption != null ? TextRenderer.MeasureText(caption, _textFont).Width + S(24) : size;
                _buttons.Add(new KeyValuePair<Btn, Rectangle>(b, new Rectangle(x, y, w, size)));
                x += w + gap;
            }
        }

        private string CaptionOf(Btn b)
        {
            if (b == Btn.Undo) return Tr.S("Отменить", "Undo");
            if (b == Btn.Gallery) return Tr.S("Галерея", "Gallery");
            if (b == Btn.Primary) return _info.PrimaryText ?? "";
            if (b == Btn.Secondary) return _info.SecondaryText ?? "";
            return null;
        }

        private int TextLeft { get { return _info.Thumb != null ? S(12) + S(160) + S(12) : S(44); } }

        private Btn ButtonAt(Point p)
        {
            foreach (KeyValuePair<Btn, Rectangle> kv in _buttons) if (kv.Value.Contains(p)) return kv.Key;
            return Btn.None;
        }

        private string TipOf(Btn b)
        {
            switch (b)
            {
                case Btn.Copy: return ImageStore.IsVideo(_info.Path) ? Tr.S("Копировать файл", "Copy the file") : Tr.S("Копировать снимок", "Copy image");
                case Btn.Folder: return Tr.S("Показать в папке", "Show in folder");
                case Btn.Delete: return Tr.S("Удалить в Корзину", "Move to Recycle Bin");
                case Btn.Edit: return Tr.S("Редактировать", "Edit");
                case Btn.Close: return Tr.S("Закрыть", "Close");
                case Btn.Gallery:
                    return ImageStore.IsVideo(_info.Path) ? Tr.S("Открыть папку со всеми видео", "Open the folder with all videos")
                                                          : Tr.S("Открыть папку со всеми снимками", "Open the folder with all screenshots");
                default: return null;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Btn b = ButtonAt(e.Location);
            if (b == _hot) return;
            _hot = b;
            Cursor = b != Btn.None || _info.Kind == ToastKind.Saved ? Cursors.Hand : Cursors.Default;
            string tip = TipOf(b);
            if (tip == null) _tip.Hide(this);
            else _tip.Show(tip, this, e.X, e.Y + S(22), 2500);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hot = Btn.None;
            _tip.Hide(this);
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Right) { CloseToast(); return; }
            if (e.Button != MouseButtons.Left) return;
            _tip.Hide(this);
            Btn b = ButtonAt(e.Location);
            switch (b)
            {
                case Btn.Close: CloseToast(); return;
                case Btn.Gallery:
                    // Папка открывается и тогда, когда сам файл уже удалён или перемещён.
                    if (GalleryRequested != null) GalleryRequested(_info.Path);
                    CloseToast();
                    return;
                case Btn.Undo:
                    _pendingDelete = false;
                    _status = null;
                    Restart();
                    return;
                case Btn.Primary:
                case Btn.Secondary:
                {
                    Action act = b == Btn.Primary ? _info.Primary : _info.Secondary;
                    if (act != null) ThreadPool.QueueUserWorkItem(delegate { try { act(); } catch (Exception ex) { CapLog.Report(ex); } });
                    CloseToast();
                    return;
                }
                case Btn.None:
                    if (_pendingDelete) return;
                    if (_info.Kind == ToastKind.Saved && File.Exists(_info.Path)) ToastHost.OpenFile(_info.Path);
                    CloseToast();
                    return;
            }
            if (!File.Exists(_info.Path))
            {
                _status = Tr.S("Файла уже нет на месте", "The file is gone");
                Restart();
                return;
            }
            switch (b)
            {
                case Btn.Edit:
                    if (EditRequested != null) EditRequested(_info.Path, _monitor);
                    CloseToast();
                    break;
                case Btn.Copy:
                    try
                    {
                        if (ImageStore.IsVideo(_info.Path)) VideoClipboard.CopyFile(_info.Path);
                        else using (Bitmap bmp = ImageStore.LoadUnlocked(_info.Path)) ImageStore.CopyImage(bmp);
                        _status = Tr.S("Скопировано в буфер обмена", "Copied to clipboard");
                    }
                    catch (Exception ex) { CapLog.Report(ex); _status = Tr.S("Не удалось скопировать", "Copy failed"); }
                    Restart();
                    break;
                case Btn.Folder:
                    ToastHost.ShowInFolder(_info.Path);
                    CloseToast();
                    break;
                case Btn.Delete:
                    // Удаление откладывается до закрытия уведомления: «Отменить» не должно доставать файл из Корзины.
                    _pendingDelete = true;
                    _status = Tr.S("Файл будет удалён в Корзину", "The file will go to the Recycle Bin");
                    _leftMs = Math.Min(_lifeMs, 5000);
                    LayoutButtons();
                    Invalidate();
                    break;
            }
        }

        private void Restart()
        {
            _leftMs = _lifeMs;
            LayoutButtons();
            Invalidate();
        }

        private void Recycle()
        {
            _pendingDelete = false;
            try
            {
                if (!File.Exists(_info.Path)) return;
                bool aborted;
                int tooLong;
                int rc = Native.RecycleFiles(IntPtr.Zero, new string[] { _info.Path }, out aborted, out tooLong);
                if (rc != 0 || tooLong > 0) CapLog.Report(new IOException("recycle failed: rc=" + rc + ", tooLong=" + tooLong + ", " + _info.Path));
            }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        // ---------- рисование ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            bool error = _info.Kind == ToastKind.Error;
            Color accent = error ? Color.FromArgb(232, 72, 85) : Color.FromArgb(59, 130, 246);
            using (Pen border = new Pen(Color.FromArgb(64, 64, 64), 1)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            using (SolidBrush strip = new SolidBrush(accent)) g.FillRectangle(strip, 1, 1, S(3), Height - 2);

            if (_info.Thumb != null)
            {
                Rectangle box = new Rectangle(S(12), (Height - S(90)) / 2, S(160), S(90));
                using (SolidBrush bg = new SolidBrush(Color.Black)) g.FillRectangle(bg, box);
                double k = Math.Min(box.Width / (double)_info.Thumb.Width, box.Height / (double)_info.Thumb.Height);
                int w = (int)(_info.Thumb.Width * k), h = (int)(_info.Thumb.Height * k);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_info.Thumb, new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h));
                if (_pendingDelete)
                    using (SolidBrush veil = new SolidBrush(Color.FromArgb(160, 32, 32, 32))) g.FillRectangle(veil, box);
            }
            else
            {
                string glyph = error ? CapFonts.Error : _info.Kind == ToastKind.Download || _info.Kind == ToastKind.Intercepted ? CapFonts.Done : CapFonts.Copy;
                TextRenderer.DrawText(g, CapFonts.Glyph(glyph, error ? "!" : "✓"), _iconFont,
                                      new Rectangle(S(12), S(12), S(24), S(24)), accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            int left = TextLeft, right = Width - S(46);
            TextFormatFlags one = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, _info.Title, _titleFont, new Rectangle(left, S(12), right - left, S(22)), Color.White, one);
            string line = _status ?? _info.Text;
            if (error)
                TextRenderer.DrawText(g, line, _textFont, new Rectangle(left, S(38), Width - left - S(14), Height - S(48)), Color.FromArgb(210, 210, 210),
                                      TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            else
                TextRenderer.DrawText(g, line, _textFont, new Rectangle(left, S(38), Width - left - S(14), S(20)),
                                      _status != null ? Color.FromArgb(160, 196, 255) : Color.FromArgb(170, 170, 170), one);

            foreach (KeyValuePair<Btn, Rectangle> kv in _buttons)
            {
                if (kv.Key == Btn.Gallery)
                {
                    // Главная кнопка уведомления — залита акцентом, как главные кнопки страниц программы.
                    using (SolidBrush fill = new SolidBrush(kv.Key == _hot ? ControlPaint.Light(accent, 0.1f) : accent)) g.FillRectangle(fill, kv.Value);
                    TextRenderer.DrawText(g, CaptionOf(kv.Key), _textFont, kv.Value, Color.White,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    continue;
                }
                if (kv.Key == _hot)
                    using (SolidBrush hot = new SolidBrush(kv.Key == Btn.Delete || kv.Key == Btn.Close && error ? Color.FromArgb(90, 196, 43, 28) : Color.FromArgb(60, 255, 255, 255)))
                        g.FillRectangle(hot, kv.Value);
                string caption = CaptionOf(kv.Key);
                if (caption != null)
                {
                    using (Pen p = new Pen(accent, 1)) g.DrawRectangle(p, kv.Value.X, kv.Value.Y, kv.Value.Width - 1, kv.Value.Height - 1);
                    TextRenderer.DrawText(g, caption, _textFont, kv.Value, Color.White,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    continue;
                }
                TextRenderer.DrawText(g, GlyphOf(kv.Key), _iconFont, kv.Value, Color.FromArgb(225, 225, 225),
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            // Полоска оставшегося времени — видно, что уведомление уйдёт само.
            int barW = (int)((Width - S(3) - 2) * Math.Max(0, _leftMs) / (double)_lifeMs);
            using (SolidBrush bar = new SolidBrush(Color.FromArgb(90, accent))) g.FillRectangle(bar, S(3) + 1, Height - S(2) - 1, barW, S(2));
        }

        private static string GlyphOf(Btn b)
        {
            switch (b)
            {
                case Btn.Copy: return CapFonts.Glyph(CapFonts.Copy, "⧉");
                case Btn.Folder: return CapFonts.Glyph(CapFonts.Folder, "▤");
                case Btn.Delete: return CapFonts.Glyph(CapFonts.Delete, "✕");
                case Btn.Edit: return CapFonts.Glyph(CapFonts.Edit, "✎");
                case Btn.Close: return CapFonts.Glyph(CapFonts.Close, "×");
                default: return "";
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_pendingDelete) Recycle();
                _timer.Dispose();
                _tip.Dispose();
                _titleFont.Dispose();
                _textFont.Dispose();
                _iconFont.Dispose();
                if (_info.Thumb != null) { _info.Thumb.Dispose(); _info.Thumb = null; }
            }
            base.Dispose(disposing);
        }
    }
}
