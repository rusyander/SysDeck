// SysDeck — «Захват»: запись видео — параметры из настроек, сессия записи, рамка области, панель с
// таймером, значок в трее на время записи, восстановление файла после сбоя.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck.Capture
{
    // Что записываем: область монитора или окно (тогда Area — его границы на момент старта).
    internal sealed class RecordTarget
    {
        public Rectangle Area;
        public IntPtr Window;
        public int Pid;
        public string App;
        public MonitorInfo Monitor;
    }

    // ------------------------------------------------------------------ //
    //  Параметры движков из настроек — без окон и устройств, проверяется тестами
    // ------------------------------------------------------------------ //
    internal static class RecordPlan
    {
        public const string Extension = ".mp4";

        public static VideoOptions Video(CapSettings s, RecordTarget t, string path)
        {
            VideoOptions o = new VideoOptions();
            o.Path = path;
            o.Window = t.Window;
            // Область — только в пределах монитора, в чётном размере (так её примет кодировщик NV12).
            Rectangle area = t.Monitor != null ? Rectangle.Intersect(t.Area, t.Monitor.Bounds) : t.Area;
            Size even = RegionMath.EvenSize(area.Size);
            o.Area = new Rectangle(area.Location, even);
            o.Fps = s.VideoFps == 30 ? 30 : 60;
            o.Codec = s.VideoCodec;
            o.Encoder = s.VideoEncoder;
            o.Quality = s.VideoQuality;
            o.OutputHeight = s.VideoHeight > 0 && s.VideoHeight < even.Height ? s.VideoHeight : 0;
            o.Cursor = s.CursorInVideo;
            if (s.MaxMinutes > 0) o.MaxDuration = TimeSpan.FromMinutes(s.MaxMinutes);
            return o;
        }

        // null — звук не нужен (выключены и система, и микрофон).
        public static AudioCaptureOptions Audio(CapSettings s, RecordTarget t)
        {
            if (!s.SystemAudio && !s.Microphone) return null;
            AudioCaptureOptions a = new AudioCaptureOptions();
            a.SystemSound = s.SystemAudio;
            // Звук только записываемого окна — когда пишется именно окно и пользователь это выбрал.
            a.ProcessId = s.SystemAudio && s.WindowAudioOnly && t.Window != IntPtr.Zero && t.Pid > 0 ? t.Pid : 0;
            a.Microphone = s.Microphone;
            a.MicDeviceId = string.IsNullOrEmpty(s.MicDeviceId) ? null : s.MicDeviceId;
            a.MicVolume = Math.Max(0, Math.Min(400, s.MicVolume)) / 100f;
            a.MicMono = s.MicMono;
            a.SeparateTracks = s.SeparateTracks && s.SystemAudio && s.Microphone;
            return a;
        }

        // Выбранный микрофон отключён (гарнитуру вынули) — пишется микрофон по умолчанию, а не тишина.
        // Возвращает предупреждение для уведомления или null.
        public static string ResolveMicrophone(AudioCaptureOptions a, List<AudioDeviceInfo> present)
        {
            if (a == null || !a.Microphone || a.MicDeviceId == null || present == null) return null;
            foreach (AudioDeviceInfo d in present)
                if (string.Equals(d.Id, a.MicDeviceId, StringComparison.OrdinalIgnoreCase)) return null;
            a.MicDeviceId = null;
            return Tr.S("выбранный микрофон не подключён — записан микрофон по умолчанию", "the selected microphone is not connected — the default one was recorded");
        }

        // Дорожка 1 — всегда смешанный звук (его играет любой плеер), 2 и 3 — система и микрофон для монтажа.
        public static List<IAudioSource> Tracks(AudioCapture capture)
        {
            List<IAudioSource> list = new List<IAudioSource>();
            if (capture == null) return list;
            if (capture.Mixed != null) list.Add(capture.Mixed);
            if (capture.SystemTrack != null) list.Add(capture.SystemTrack);
            if (capture.MicTrack != null) list.Add(capture.MicTrack);
            return list;
        }

        public static string FormatElapsed(TimeSpan t)
        {
            int total = (int)Math.Max(0, Math.Floor(t.TotalSeconds));
            int h = total / 3600, m = total / 60 % 60, sec = total % 60;
            return h > 0
                ? h.ToString(CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture) + ":" + sec.ToString("00", CultureInfo.InvariantCulture)
                : m.ToString("00", CultureInfo.InvariantCulture) + ":" + sec.ToString("00", CultureInfo.InvariantCulture);
        }

        public static string FormatBytes(long bytes)
        {
            double mb = bytes / (1024.0 * 1024.0);
            if (mb >= 1024) return (mb / 1024).ToString("0.0", CultureInfo.CurrentCulture) + Tr.S(" ГБ", " GB");
            return mb.ToString(mb >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture) + Tr.S(" МБ", " MB");
        }

        // Пояснение к коду остановки движка: пользователь видит его в уведомлении.
        public static string ReasonText(string reason, bool window)
        {
            // Коды VideoRecorder.StopReason: stopped, limit-duration, limit-size, low-disk, device-lost, error: …
            string r = (reason ?? "").Trim();
            switch (r)
            {
                case "": case "stopped": return null;
                case "limit-duration": return Tr.S("достигнута максимальная длительность", "the maximum duration was reached");
                case "limit-size": return Tr.S("достигнут предельный размер файла", "the file size limit was reached");
                case "low-disk": return Tr.S("на диске осталось меньше 1 ГБ", "less than 1 GB left on the disk");
                case "device-lost":
                    return window ? Tr.S("записываемое окно закрылось или свернулось", "the recorded window was closed or minimized")
                                  : Tr.S("видеоустройство перезапустилось (драйвер или режим экрана)", "the graphics device was reset (driver or display mode)");
            }
            if (r.StartsWith("error:", StringComparison.Ordinal)) return Tr.S("ошибка записи: ", "recording error: ") + r.Substring(6).Trim();
            return r;
        }
    }

    // ------------------------------------------------------------------ //
    //  Метка «идёт запись»: пережила процесс — значит, запись оборвалась, и файл нужно починить
    // ------------------------------------------------------------------ //
    internal static class RecordMarker
    {
        public static string File { get { return CapPaths.RecordingFile; } }

        public static void Set(string path)
        {
            try { CapPaths.WriteAtomic(File, path); }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        public static void Clear()
        {
            try { if (System.IO.File.Exists(File)) System.IO.File.Delete(File); }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        public static string Pending()
        {
            try { return System.IO.File.Exists(File) ? System.IO.File.ReadAllText(File).Trim() : null; }
            catch { return null; }
        }

        // Возвращает путь восстановленного файла или null. Исходник заменяется только проверенной копией
        // (Repair открывает результат и требует длительность > 0); прежний файл уходит в Корзину.
        public static string Recover(string path, out string problem)
        {
            problem = null;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path) || !ImageStore.IsVideo(path)) return null;
            string repaired;
            if (!FragmentedMp4.Repair(path, out repaired) || repaired == null || !System.IO.File.Exists(repaired))
            {
                problem = Tr.S("файл не удалось восстановить", "the file could not be repaired");
                return null;
            }
            bool aborted;
            int tooLong;
            int rc = Native.RecycleFiles(IntPtr.Zero, new string[] { path }, out aborted, out tooLong);
            if (rc != 0 || System.IO.File.Exists(path))
            {
                // Исходник не ушёл в Корзину — остаются оба файла, починенный рядом.
                problem = Tr.S("восстановленная копия лежит рядом", "the repaired copy is next to it");
                return repaired;
            }
            System.IO.File.Move(repaired, path);
            return path;
        }
    }

    // ------------------------------------------------------------------ //
    //  Сессия записи. Живёт в потоке интерфейса агента; запуск и остановка движка — в фоновом потоке
    // ------------------------------------------------------------------ //
    internal sealed class RecordSession : IDisposable
    {
        private enum State { Countdown, Starting, Recording, Stopping, Done }

        private readonly CapSettings _settings;
        private readonly RecordTarget _target;
        private readonly Action<Action> _post;
        private readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer();
        private State _state = State.Countdown;
        private int _countdown;
        private string _path;
        private Bitmap _thumb;
        private VideoRecorder _recorder;
        private AudioCapture _audio;
        private RecordPanel _panel;
        private RecordFrame _frame;
        private RecordTrayIcon _tray;
        private bool _stopAfterStart, _blackWarned;
        private readonly List<string> _warnings = new List<string>();
        private DateTime _countdownEnds;

        // Чем закончилась запись: уведомление показывает агент.
        public event Action<RecordSession, ToastInfo> Completed;

        public RecordSession(CapSettings settings, RecordTarget target, Action<Action> post)
        {
            _settings = settings;
            _target = target;
            _post = post;
            _tick.Interval = 250;
            _tick.Tick += delegate { CapLog.Swallow(OnTick); };
        }

        public RecordTarget Target { get { return _target; } }
        public bool IsActive { get { return _state != State.Done; } }
        public bool IsPaused { get { VideoRecorder r = _recorder; return r != null && r.Paused; } }

        public void Begin()
        {
            _frame = _settings.RecordPanel && _target.Window == IntPtr.Zero ? RecordFrame.Around(_target.Area, _target.Monitor) : null;
            if (_settings.RecordPanel)
            {
                _panel = new RecordPanel(_target.Area, _target.Monitor);
                _panel.PauseClicked += TogglePause;
                _panel.StopClicked += RequestStop;
                if (!_panel.SafeToShow) { _panel.Dispose(); _panel = null; }
            }
            _tray = new RecordTrayIcon();
            _tray.PauseClicked += TogglePause;
            _tray.StopClicked += RequestStop;
            _tick.Start();
            _countdown = _settings.CountdownSeconds;
            if (_countdown > 0)
            {
                _countdownEnds = DateTime.UtcNow.AddSeconds(_countdown);
                UpdateUi();
                if (_panel != null) _panel.Show();
                return;
            }
            StartEngine();
            if (_panel != null) _panel.Show();
        }

        private void StartEngine()
        {
            _state = State.Starting;
            UpdateUi();
            CapSettings s = _settings;
            RecordTarget t = _target;
            // Миниатюра для уведомления — кадр области в момент старта (окна агента исключены из захвата).
            try
            {
                Rectangle grab = t.Window != IntPtr.Zero ? Rectangle.Intersect(CapNative.VisualBounds(t.Window), t.Monitor != null ? t.Monitor.Bounds : t.Area) : t.Area;
                if (grab.Width > 0 && grab.Height > 0)
                    using (Bitmap frame = ScreenGrab.Grab(grab, false)) _thumb = ToastHost.MakeThumbnail(frame);
            }
            catch (Exception ex) { CapLog.Report(ex); }

            try
            {
                _path = NameTemplate.BuildPath(s.EffectiveVideoFolder, s.PerAppFolders, t.App, s.NameTemplate, DateTime.Now, RecordPlan.Extension, null);
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
            }
            catch (Exception ex) { Finish(Failure(ex)); return; }

            string path = _path;
            Thread worker = new Thread(delegate()
            {
                AudioCapture audio = null;
                VideoRecorder recorder = null;
                Exception error = null;
                List<string> warnings = new List<string>();
                try
                {
                    VideoOptions vo = RecordPlan.Video(s, t, path);
                    AudioCaptureOptions ao = RecordPlan.Audio(s, t);
                    if (ao != null)
                    {
                        string micNote = RecordPlan.ResolveMicrophone(ao, ao.Microphone && ao.MicDeviceId != null ? AudioDevices.Microphones() : null);
                        if (micNote != null) warnings.Add(micNote);
                        try
                        {
                            audio = AudioCapture.Create(ao);
                            warnings.AddRange(AudioWarnings(audio, ao));
                        }
                        catch (AudioException ex)
                        {
                            CapLog.Report(ex);
                            warnings.Add(AudioReasonText(ex.Reason));
                        }
                        vo.AudioTracks.AddRange(RecordPlan.Tracks(audio));
                    }
                    RecordMarker.Set(path);
                    recorder = VideoRecorder.Start(vo);
                }
                catch (Exception ex)
                {
                    error = ex;
                    RecordMarker.Clear();
                    if (audio != null) CapLog.Swallow(audio.Dispose);
                    audio = null;
                }
                _post(delegate { EngineStarted(recorder, audio, error, warnings); });
            });
            worker.Name = "wpc-record-start";
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        private static IEnumerable<string> AudioWarnings(AudioCapture audio, AudioCaptureOptions o)
        {
            List<string> list = new List<string>();
            foreach (string code in audio.Unavailable) list.Add(AudioReasonText(code));
            if (audio.ProcessLoopbackFallback && o.ProcessId > 0 && !audio.Unavailable.Contains("process-loopback-unsupported"))
                list.Add(Tr.S("звук только окна недоступен — пишется весь звук системы", "window-only sound is unavailable — recording all system sound"));
            return list;
        }

        internal static string AudioReasonText(string code)
        {
            switch (code ?? "")
            {
                case "mic-access-denied": return Tr.S("микрофон запрещён в «Параметры → Конфиденциальность → Микрофон»", "the microphone is blocked in Settings → Privacy → Microphone");
                case "mic-no-device": return Tr.S("микрофон не найден — запись без него", "no microphone found — recording without it");
                case "mic-device-in-use": return Tr.S("микрофон занят другой программой", "the microphone is used exclusively by another app");
                case "system-no-device": return Tr.S("нет устройства вывода звука — запись без звука системы", "no audio output device — recording without system sound");
                case "system-device-in-use": return Tr.S("устройство вывода занято другой программой", "the audio output is used exclusively by another app");
                case "system-no-service": case "mic-no-service": return Tr.S("служба «Windows Audio» не работает", "the Windows Audio service is not running");
                case "process-loopback-unsupported": return Tr.S("звук только окна — с Windows 10 2004; пишется весь звук", "window-only sound needs Windows 10 2004+; recording all sound");
                case "process-not-found": return Tr.S("процесс окна завершился — звук окна не пишется", "the window's process has exited — no window sound");
                case "nothing-selected": return Tr.S("звук не выбран", "no sound selected");
                default: return Tr.S("звук: ", "sound: ") + code;
            }
        }

        private void EngineStarted(VideoRecorder recorder, AudioCapture audio, Exception error, List<string> warnings)
        {
            if (error != null || recorder == null)
            {
                CapLog.Report(error);
                TryDeleteEmpty(_path);
                Finish(Failure(error));
                return;
            }
            _recorder = recorder;
            _audio = audio;
            _state = State.Recording;
            _warnings.AddRange(warnings);
            CapLog.Write("record: " + recorder.SourceKind + " · " + recorder.EncoderPath + " · " + recorder.Codec + " " +
                         recorder.OutputSize.Width + "x" + recorder.OutputSize.Height + (recorder.CrashSafe ? " fMP4" : " MP4") + " · " + _path);
            if (!recorder.EncoderHardware) _warnings.Add(Tr.S("аппаратный кодировщик не прошёл проверку — запись идёт программным", "no hardware encoder passed the check — using the software one"));
            if (_stopAfterStart) { RequestStop(); return; }
            UpdateUi();
        }

        private void OnTick()
        {
            if (_state == State.Countdown)
            {
                int left = (int)Math.Ceiling((_countdownEnds - DateTime.UtcNow).TotalSeconds);
                if (left <= 0) StartEngine();
                else if (left != _countdown) { _countdown = left; UpdateUi(); }
                return;
            }
            if (_state != State.Recording) return;
            VideoRecorder r = _recorder;
            if (r.Finished) { RequestStop(); return; }
            if (!_blackWarned && r.BlackFramesDetected)
            {
                _blackWarned = true;
                _warnings.Add(Tr.S("кадр чёрный: игра в эксклюзивном полноэкранном режиме или защищённое видео — включите «Оконный без рамки»",
                                   "the picture is black: exclusive fullscreen or protected video — switch the game to borderless window"));
            }
            UpdateUi();
        }

        private void UpdateUi()
        {
            string title, detail;
            VideoRecorder r = _recorder;
            switch (_state)
            {
                case State.Countdown:
                    title = Tr.S("Запись через ", "Recording in ") + _countdown.ToString(CultureInfo.InvariantCulture);
                    detail = "";
                    break;
                case State.Starting:
                    title = Tr.S("Запуск…", "Starting…");
                    detail = "";
                    break;
                case State.Stopping:
                    title = Tr.S("Сохраняю…", "Saving…");
                    detail = "";
                    break;
                default:
                    title = RecordPlan.FormatElapsed(r != null ? r.Elapsed : TimeSpan.Zero);
                    detail = r != null ? RecordPlan.FormatBytes(r.BytesWritten) : "";
                    break;
            }
            bool paused = r != null && r.Paused;
            if (_panel != null) _panel.SetState(title, detail, _state == State.Recording, paused);
            if (_tray != null) _tray.SetState(title, _state == State.Recording, paused);
        }

        public void TogglePause()
        {
            VideoRecorder r = _recorder;
            if (_state != State.Recording || r == null) return;
            if (r.Paused) r.Resume(); else r.Pause();
            UpdateUi();
        }

        // Повторное нажатие клавиши, «Стоп» на панели или в трее, остановка самим движком.
        public void RequestStop()
        {
            switch (_state)
            {
                case State.Countdown: Finish(null); return;
                case State.Starting: _stopAfterStart = true; return;
                case State.Recording: break;
                default: return;
            }
            _state = State.Stopping;
            UpdateUi();
            VideoRecorder recorder = _recorder;
            AudioCapture audio = _audio;
            string path = _path;
            Thread worker = new Thread(delegate()
            {
                try { recorder.Stop(); } catch (Exception ex) { CapLog.Report(ex); }
                if (audio != null) CapLog.Swallow(audio.Dispose);
                _post(delegate { Stopped(recorder, path); });
            });
            worker.Name = "wpc-record-stop";
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        // Выход агента: остановить запись синхронно, чтобы Finalize успел закрыть файл.
        public void StopNow()
        {
            _tick.Stop();
            VideoRecorder recorder = _recorder;
            if (recorder != null && (_state == State.Recording || _state == State.Stopping))
            {
                try { recorder.Stop(); } catch (Exception ex) { CapLog.Report(ex); }
                if (_audio != null) CapLog.Swallow(_audio.Dispose);
                RecordMarker.Clear();
            }
            _state = State.Done;
            CloseUi();
        }

        private void Stopped(VideoRecorder recorder, string path)
        {
            RecordMarker.Clear();
            long bytes = 0;
            try { if (File.Exists(path)) bytes = new FileInfo(path).Length; } catch { }
            string reason = RecordPlan.ReasonText(recorder.StopReason, _target.Window != IntPtr.Zero);
            CapLog.Write("record: stopped " + RecordPlan.FormatElapsed(recorder.Elapsed) + ", frames " + recorder.FramesWritten +
                         " (unique " + recorder.UniqueFrames + ", dropped " + recorder.DroppedFrames + "), reason " + (recorder.StopReason ?? "user") +
                         (recorder.Notes.Length > 0 ? ", notes " + recorder.Notes : ""));
            if (bytes <= 0)
            {
                Finish(ToastInfo.Error(Tr.S("Видео не записано", "The video was not recorded"), reason ?? Tr.S("файл пуст", "the file is empty")));
                return;
            }
            List<string> notes = new List<string>(_warnings);
            if (reason != null) notes.Insert(0, reason);
            Bitmap thumb = _thumb;
            _thumb = null;
            Finish(ToastInfo.VideoSaved(thumb, path, recorder.Elapsed, bytes, notes.Count > 0 ? notes[0] : null));
        }

        private static ToastInfo Failure(Exception ex)
        {
            string text = ex == null ? "" : ex is UnauthorizedAccessException ? Tr.S("нет доступа к папке", "no access to the folder") : ex.Message;
            return ToastInfo.Error(Tr.S("Не удалось начать запись", "Could not start recording"), text);
        }

        private static void TryDeleteEmpty(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path) && new FileInfo(path).Length == 0) File.Delete(path); }
            catch { }
        }

        private void Finish(ToastInfo toast)
        {
            _state = State.Done;
            _tick.Stop();
            CloseUi();
            Action<RecordSession, ToastInfo> done = Completed;
            if (done != null) done(this, toast);
            else if (toast != null && toast.Thumb != null) toast.Thumb.Dispose();
        }

        private void CloseUi()
        {
            if (_panel != null) { _panel.Dispose(); _panel = null; }
            if (_frame != null) { _frame.Dispose(); _frame = null; }
            if (_tray != null) { _tray.Dispose(); _tray = null; }
        }

        public void Dispose()
        {
            if (_state != State.Done) StopNow();
            _tick.Dispose();
            if (_thumb != null) { _thumb.Dispose(); _thumb = null; }
        }
    }

    // ------------------------------------------------------------------ //
    //  Окна поверх записи: без активации, прозрачны для мыши где нужно, исключены из захвата
    // ------------------------------------------------------------------ //
    internal abstract class RecordOverlayWindow : Form
    {
        public bool ExcludedFromCapture { get; private set; }

        protected RecordOverlayWindow()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= CapNative.WS_EX_TOOLWINDOW | CapNative.WS_EX_NOACTIVATE | CapNative.WS_EX_TOPMOST | ExtraExStyle;
                return cp;
            }
        }

        protected virtual int ExtraExStyle { get { return 0; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Windows 10 2004+: окно не попадает ни в запись, ни на снимки. Раньше — false, и вызывающий решает.
            try { ExcludedFromCapture = CapNative.SetWindowDisplayAffinity(Handle, CapNative.WDA_EXCLUDEFROMCAPTURE); }
            catch { ExcludedFromCapture = false; }
        }

        protected const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = new IntPtr(MA_NOACTIVATE); return; }
            base.WndProc(ref m);
        }
    }

    // Рамка вокруг области: четыре полоски СНАРУЖИ области, поэтому в кадр не попадают даже без исключения из захвата.
    internal sealed class RecordFrame : IDisposable
    {
        private readonly List<Strip> _strips = new List<Strip>();

        private sealed class Strip : RecordOverlayWindow
        {
            public Strip(Rectangle bounds)
            {
                BackColor = Color.FromArgb(229, 57, 53);
                Bounds = bounds;
            }

            protected override int ExtraExStyle { get { return CapNative.WS_EX_TRANSPARENT | CapNative.WS_EX_LAYERED; } }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                SetLayeredWindowAttributes(Handle, 0, 230, 2);
            }

            [DllImport("user32.dll")]
            private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
        }

        // null — рамку ставить некуда (область во весь монитор).
        public static RecordFrame Around(Rectangle area, MonitorInfo mon)
        {
            Rectangle bounds = mon != null ? mon.Bounds : Rectangle.Empty;
            int t = Math.Max(2, (int)Math.Round(2 * CapDpi.ScaleAt(area.Location)));
            RecordFrame f = new RecordFrame();
            Rectangle outer = Rectangle.Inflate(area, t, t);
            Rectangle[] parts =
            {
                new Rectangle(outer.Left, outer.Top, outer.Width, t),
                new Rectangle(outer.Left, area.Bottom, outer.Width, t),
                new Rectangle(outer.Left, area.Top, t, area.Height),
                new Rectangle(area.Right, area.Top, t, area.Height)
            };
            foreach (Rectangle p in parts)
            {
                Rectangle r = bounds.IsEmpty ? p : Rectangle.Intersect(p, bounds);
                if (r.Width <= 0 || r.Height <= 0 || r.IntersectsWith(area)) continue;
                Strip s = new Strip(r);
                f._strips.Add(s);
                s.Show();
                s.Bounds = r;
            }
            if (f._strips.Count == 0) { f.Dispose(); return null; }
            return f;
        }

        public void Dispose()
        {
            foreach (Strip s in _strips) s.Dispose();
            _strips.Clear();
        }
    }

    // Панель записи: ● таймер · размер · пауза · стоп. Перетаскивается мышью.
    internal sealed class RecordPanel : RecordOverlayWindow
    {
        private readonly float _scale;
        private readonly Font _timeFont, _detailFont, _glyphFont;
        private readonly Rectangle _area;
        private string _title = "", _detail = "";
        private bool _recording, _paused, _blink;
        private int _hot = -1;
        private Rectangle _pauseRect, _stopRect;
        private Point _dragFrom;
        private bool _dragging;
        private readonly ToolTip _tip = new ToolTip();
        private readonly System.Windows.Forms.Timer _blinkTimer = new System.Windows.Forms.Timer();

        public event Action PauseClicked;
        public event Action StopClicked;

        public RecordPanel(Rectangle area, MonitorInfo mon)
        {
            _area = area;
            _scale = Math.Max(1f, CapDpi.ScaleAt(area.Location));
            _timeFont = new Font("Segoe UI Semibold", 14f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _detailFont = new Font("Segoe UI", 12f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _glyphFont = CapFonts.Icons(14f * _scale);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.FromArgb(32, 32, 32);
            Size size = new Size(S(236), S(40));
            Rectangle work = mon != null ? mon.Bounds : Screen.FromPoint(area.Location).Bounds;
            Bounds = Place(area, work, size, S(8));
            _pauseRect = new Rectangle(Width - S(76), S(5), S(30), S(30));
            _stopRect = new Rectangle(Width - S(40), S(5), S(30), S(30));
            _blinkTimer.Interval = 600;
            _blinkTimer.Tick += delegate { _blink = !_blink; Invalidate(new Rectangle(0, 0, S(30), Height)); };
            _blinkTimer.Start();
            IntPtr force = Handle;
        }

        private int S(float v) { return (int)Math.Round(v * _scale); }

        // Под областью, если там есть место; иначе над ней; иначе внутри монитора внизу по центру области.
        internal static Rectangle Place(Rectangle area, Rectangle monitor, Size size, int gap)
        {
            int x = Math.Max(monitor.Left, Math.Min(monitor.Right - size.Width, area.Left + (area.Width - size.Width) / 2));
            if (area.Bottom + gap + size.Height <= monitor.Bottom) return new Rectangle(x, area.Bottom + gap, size.Width, size.Height);
            if (area.Top - gap - size.Height >= monitor.Top) return new Rectangle(x, area.Top - gap - size.Height, size.Width, size.Height);
            return new Rectangle(x, monitor.Bottom - size.Height - gap * 6, size.Width, size.Height);
        }

        // Панель внутри области без исключения из захвата (Windows 10 до 2004) попала бы в видео — тогда её нет,
        // остаётся значок в трее.
        public bool SafeToShow { get { return ExcludedFromCapture || !Bounds.IntersectsWith(_area); } }

        public void SetState(string title, string detail, bool recording, bool paused)
        {
            if (title == _title && detail == _detail && recording == _recording && paused == _paused) return;
            _title = title; _detail = detail; _recording = recording; _paused = paused;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (Pen border = new Pen(Color.FromArgb(70, 70, 70))) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            Color dot = _paused ? Color.FromArgb(255, 183, 77) : Color.FromArgb(229, 57, 53);
            if (!_recording || _paused || !_blink)
                using (SolidBrush b = new SolidBrush(_recording ? dot : Color.FromArgb(120, 120, 120))) g.FillEllipse(b, S(12), (Height - S(12)) / 2, S(12), S(12));
            using (SolidBrush fg = new SolidBrush(Color.White))
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(170, 170, 170)))
            {
                SizeF t = g.MeasureString(_title, _timeFont);
                g.DrawString(_title, _timeFont, fg, S(30), (Height - t.Height) / 2);
                if (_detail.Length > 0)
                {
                    SizeF d = g.MeasureString(_detail, _detailFont);
                    g.DrawString(_detail, _detailFont, dim, S(30) + t.Width + S(6), (Height - d.Height) / 2);
                }
            }
            if (_recording)
            {
                DrawButton(g, _pauseRect, _paused ? CapFonts.Glyph(CapFonts.Play, "▶") : CapFonts.Glyph(CapFonts.Pause, "II"), _hot == 0);
            }
            DrawButton(g, _stopRect, CapFonts.Glyph(CapFonts.Stop, "■"), _hot == 1);
        }

        private void DrawButton(Graphics g, Rectangle r, string glyph, bool hot)
        {
            if (hot) using (SolidBrush b = new SolidBrush(Color.FromArgb(64, 64, 64))) g.FillRectangle(b, r);
            using (SolidBrush fg = new SolidBrush(Color.White))
            {
                SizeF s = g.MeasureString(glyph, _glyphFont);
                g.DrawString(glyph, _glyphFont, fg, r.X + (r.Width - s.Width) / 2, r.Y + (r.Height - s.Height) / 2);
            }
        }

        private int ButtonAt(Point p)
        {
            if (_recording && _pauseRect.Contains(p)) return 0;
            if (_stopRect.Contains(p)) return 1;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                Point now = Cursor.Position;
                Location = new Point(Left + now.X - _dragFrom.X, Top + now.Y - _dragFrom.Y);
                _dragFrom = now;
                return;
            }
            int b = ButtonAt(e.Location);
            if (b == _hot) return;
            _hot = b;
            Cursor = b >= 0 ? Cursors.Hand : Cursors.SizeAll;
            if (b == 0) _tip.Show(_paused ? Tr.S("Продолжить", "Resume") : Tr.S("Пауза", "Pause"), this, e.X, e.Y + S(24), 2000);
            else if (b == 1) _tip.Show(Tr.S("Остановить и сохранить", "Stop and save"), this, e.X, e.Y + S(24), 2000);
            else _tip.Hide(this);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hot = -1;
            _tip.Hide(this);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (ButtonAt(e.Location) >= 0) return;
            _dragging = true;
            _dragFrom = Cursor.Position;
            Capture = true;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging) { _dragging = false; Capture = false; return; }
            if (e.Button != MouseButtons.Left) return;
            int b = ButtonAt(e.Location);
            Action handler = b == 0 ? PauseClicked : b == 1 ? StopClicked : null;
            if (handler != null) CapLog.Swallow(delegate { handler(); });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _blinkTimer.Dispose();
                _tip.Dispose();
                _timeFont.Dispose();
                _detailFont.Dispose();
                _glyphFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // Значок в трее только на время записи: остановить можно, даже когда панель скрыта (весь экран, игра).
    internal sealed class RecordTrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon = new NotifyIcon();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem _pause, _stop;
        private readonly Icon _red, _amber;
        private bool _paused;

        public event Action PauseClicked;
        public event Action StopClicked;

        public RecordTrayIcon()
        {
            _red = MakeIcon(Color.FromArgb(229, 57, 53));
            _amber = MakeIcon(Color.FromArgb(255, 183, 77));
            _pause = new ToolStripMenuItem(Tr.S("Пауза", "Pause"));
            _pause.Click += delegate { Raise(PauseClicked); };
            _stop = new ToolStripMenuItem(Tr.S("Остановить и сохранить", "Stop and save"));
            _stop.Click += delegate { Raise(StopClicked); };
            _menu.Items.Add(_pause);
            _menu.Items.Add(_stop);
            _icon.ContextMenuStrip = _menu;
            _icon.Icon = _red;
            _icon.Text = Tr.S("Идёт запись", "Recording");
            _icon.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Raise(StopClicked); };
            _icon.Visible = true;
        }

        private static void Raise(Action a) { if (a != null) CapLog.Swallow(delegate { a(); }); }

        private static Icon MakeIcon(Color color)
        {
            using (Bitmap b = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (SolidBrush brush = new SolidBrush(color)) g.FillEllipse(brush, 2, 2, 12, 12);
                }
                IntPtr h = b.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { DestroyIcon(h); }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        public void SetState(string title, bool recording, bool paused)
        {
            string text = Tr.S("Запись · ", "Recording · ") + title + (paused ? Tr.S(" · пауза", " · paused") : "") +
                          Tr.S(" (щелчок — стоп)", " (click to stop)");
            if (text.Length > 63) text = text.Substring(0, 63);
            if (_icon.Text != text) _icon.Text = text;
            _pause.Enabled = recording;
            if (paused != _paused)
            {
                _paused = paused;
                _icon.Icon = paused ? _amber : _red;
                _pause.Text = paused ? Tr.S("Продолжить", "Resume") : Tr.S("Пауза", "Pause");
            }
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
            _red.Dispose();
            _amber.Dispose();
        }
    }

    // Файл видео в буфер обмена — как «Копировать» в Проводнике: вставляется в мессенджер или папку.
    internal static class VideoClipboard
    {
        public static void CopyFile(string path)
        {
            StringCollection files = new StringCollection();
            files.Add(path);
            Clipboard.SetFileDropList(files);
        }
    }
}
