// SysDeck — «Захват»: сведение звука для записи, дорожка для видео и индикатор микрофона.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace SysDeck.Capture
{
    internal sealed class AudioCaptureOptions
    {
        public bool SystemSound = true;
        public string OutputDeviceId = null;// null — устройство вывода по умолчанию (loopback)
        public int ProcessId = 0;         // >0 — звук только этого процесса с дочерним деревом
        public bool Microphone = false;
        public string MicDeviceId = null; // null — микрофон по умолчанию
        public float MicVolume = 1f;
        public bool MicMono = false;       // свести микрофон в моно на оба канала
        public float SystemVolume = 1f;
        public bool SeparateTracks = false;// дополнительно SystemTrack / MicTrack
    }

    internal sealed class AudioCapture : IDisposable
    {
        private static int _plSupported = -1;

        private readonly AudioCaptureOptions _o;
        private AudioStream _sys, _mic;
        private readonly List<AudioTrackSource> _sources = new List<AudioTrackSource>();
        private readonly object _gate = new object();
        private bool _hasOrigin;
        private long _origin100;
        private readonly List<long[]> _pauses = new List<long[]>();   // [начало QPC, конец QPC или MaxValue]
        private DeviceNotifications _notify;
        private AudioInterop.IMMDeviceEnumerator _notifyEnum;
        private bool _disposed;

        // Коды недоступных источников на момент Create: system-no-device, system-device-in-use, system-error,
        // mic-no-device, mic-access-denied, mic-device-in-use, mic-error, process-loopback-unsupported,
        // process-not-found, process-error. Недоступный источник пишет тишину и продолжает попытки открыться.
        public readonly List<string> Unavailable = new List<string>();
        public readonly List<string> UnavailableMessages = new List<string>();
        // Запрошен звук процесса, но пишется весь системный звук (старая Windows или сбой активации).
        public bool ProcessLoopbackFallback { get; private set; }

        public IAudioSource Mixed { get; private set; }
        public IAudioSource SystemTrack { get; private set; }
        public IAudioSource MicTrack { get; private set; }

        // Вызывается из потока звука: обработчик сам переносит работу в свой поток.
        public event Action<string> DeviceLost;

        private AudioCapture(AudioCaptureOptions o) { _o = o; }

        // Loopback процесса: AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK появился в Windows 10 2004 (19041).
        public static bool ProcessLoopbackSupported
        {
            get
            {
                if (_plSupported < 0)
                {
                    int build = 0;
                    try
                    {
                        AudioInterop.OsVersionInfo v = new AudioInterop.OsVersionInfo();
                        v.Size = Marshal.SizeOf(typeof(AudioInterop.OsVersionInfo));
                        if (AudioInterop.RtlGetVersion(ref v) == 0 && v.Major >= 10) build = v.Build;
                    }
                    catch { }
                    _plSupported = build >= 19041 ? 1 : 0;
                }
                return _plSupported == 1;
            }
        }

        public static AudioCapture Create(AudioCaptureOptions o)
        {
            if (o == null) throw new ArgumentNullException("o");
            if (!o.SystemSound && !o.Microphone)
                throw new AudioException("nothing-selected", "Neither system sound nor microphone is selected.", 0);
            AudioCapture c = new AudioCapture(o);
            try
            {
                int opened = 0, requested = 0;
                if (o.SystemSound)
                {
                    requested++;
                    AudioException err = null;
                    if (o.ProcessId > 0 && ProcessLoopbackSupported)
                    {
                        c._sys = c.NewStream(AudioStreamKind.ProcessLoopback, null, o.ProcessId);
                        err = c._sys.StartAndWait(true);
                        if (err != null && err.Reason != "process-not-found")
                        {
                            // Активация звука процесса не удалась: пишем весь системный звук и говорим об этом.
                            c.AddUnavailable(err);
                            c._sys.Dispose();
                            c._sys = null;
                            c.ProcessLoopbackFallback = true;
                        }
                    }
                    else if (o.ProcessId > 0)
                    {
                        c.AddUnavailable(new AudioException("process-loopback-unsupported",
                            "Per-application audio capture requires Windows 10 version 2004 (build 19041) or newer; recording all system sound instead.", 0));
                        c.ProcessLoopbackFallback = true;
                    }
                    if (c._sys == null)
                    {
                        c._sys = c.NewStream(AudioStreamKind.SystemLoopback, o.OutputDeviceId, 0);
                        err = c._sys.StartAndWait(true);
                    }
                    if (err != null) c.AddUnavailable(err); else opened++;
                }
                if (o.Microphone)
                {
                    requested++;
                    c._mic = c.NewStream(AudioStreamKind.Microphone, o.MicDeviceId, 0);
                    AudioException err = c._mic.StartAndWait(true);
                    if (err != null) c.AddUnavailable(err); else opened++;
                    if (AudioDevices.MicrophonePrivacyDenied())
                        c.AddUnavailable(new AudioException("mic-access-denied",
                            "Microphone access is turned off in Windows Settings > Privacy > Microphone (allow desktop apps).", 0));
                }
                if (opened == 0)
                    throw new AudioException(string.Join(",", c.Unavailable.ToArray()),
                        "No audio source could be opened: " + string.Join(" ", c.UnavailableMessages.ToArray()), 0);

                float sg = Clamp(o.SystemVolume), mg = Clamp(o.MicVolume);
                c.Mixed = c.AddSource("mixed", c._sys, sg, c._mic, mg, o.MicMono);
                if (o.SeparateTracks && c._sys != null) c.SystemTrack = c.AddSource("system", c._sys, sg, null, 0f, false);
                if (o.SeparateTracks && c._mic != null) c.MicTrack = c.AddSource("mic", null, 0f, c._mic, mg, o.MicMono);
                c.RegisterNotifications();
                return c;
            }
            catch
            {
                c.Dispose();
                throw;
            }
        }

        private static float Clamp(float v)
        {
            if (float.IsNaN(v) || v < 0f) return 0f;
            return v > 4f ? 4f : v;
        }

        private AudioStream NewStream(AudioStreamKind kind, string id, int pid)
        {
            AudioStream s = new AudioStream(kind, id, pid);
            s.LostCallback = OnStreamLost;
            return s;
        }

        private AudioTrackSource AddSource(string name, AudioStream sys, float sg, AudioStream mic, float mg, bool mono)
        {
            AudioTrackSource t = new AudioTrackSource(this, name, sys, sg, mic, mg, mono);
            lock (_gate) _sources.Add(t);
            return t;
        }

        private void AddUnavailable(AudioException e)
        {
            if (Unavailable.Contains(e.Reason)) return;
            Unavailable.Add(e.Reason);
            UnavailableMessages.Add(e.Message);
            CapLog.Write("audio: unavailable " + e.Reason + ": " + e.Message);
        }

        private void OnStreamLost(AudioStream s, string message)
        {
            string text = (s.Kind == AudioStreamKind.Microphone ? "Microphone: " : s.Kind == AudioStreamKind.ProcessLoopback ? "Application sound: " : "System sound: ") + message;
            CapLog.Write("audio: device lost: " + text);
            Action<string> h = DeviceLost;
            if (h == null) return;
            try { h(text); }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        private IEnumerable<AudioStream> Streams()
        {
            if (_sys != null) yield return _sys;
            if (_mic != null) yield return _mic;
        }

        private void RegisterNotifications()
        {
            bool followsDefault = (_sys != null && _sys.Kind == AudioStreamKind.SystemLoopback && _sys.DeviceId == null) ||
                                  (_mic != null && _mic.DeviceId == null);
            if (!followsDefault) return;
            try
            {
                AudioInterop.RunMta(delegate()
                {
                    AudioInterop.IMMDeviceEnumerator en = (AudioInterop.IMMDeviceEnumerator)new AudioInterop.MMDeviceEnumeratorCo();
                    DeviceNotifications n = new DeviceNotifications(this);
                    if (en.RegisterEndpointNotificationCallback(n) >= 0) { _notifyEnum = en; _notify = n; }
                    else AudioInterop.Release(en);
                });
            }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        // Переоткрыть все потоки (например, пользователь сменил устройство); запись продолжается без разрыва шкалы.
        public void Reopen()
        {
            foreach (AudioStream s in Streams()) s.RequestReopen();
        }

        public long Reinits
        {
            get { long n = 0; foreach (AudioStream s in Streams()) n += s.Reinits; return n; }
        }

        internal void OnDefaultDeviceChanged(int flow, int role, string id)
        {
            if (role != AudioInterop.EConsole) return;
            foreach (AudioStream s in Streams()) s.NotifyDefaultChanged(flow, id);
        }

        // Первый Start любого источника задаёт начало общей шкалы.
        internal long EnsureOrigin(long startQpc)
        {
            lock (_gate)
            {
                if (!_hasOrigin)
                {
                    _hasOrigin = true;

                    _origin100 = AudioStream.Qpc100(startQpc);
                    foreach (AudioStream s in Streams()) s.Timeline.SetOrigin(_origin100);
                }
                return _origin100;
            }
        }

        // Остановить все дорожки одной меткой QPC (длины совпадут кадр в кадр); ждёт до 1 с.
        public void Stop(long stopQpc)
        {
            List<AudioTrackSource> list;
            lock (_gate) list = new List<AudioTrackSource>(_sources);
            foreach (AudioTrackSource t in list) t.Stop(stopQpc);
        }

        public void Pause() { Pause(Stopwatch.GetTimestamp()); }

        public void Resume() { Resume(Stopwatch.GetTimestamp()); }

        // qpc — та же метка, что видеопоток использует для своей паузы (Stopwatch.GetTimestamp()).
        public void Pause(long qpc)
        {
            lock (_gate)
            {
                if (_pauses.Count > 0 && _pauses[_pauses.Count - 1][1] == long.MaxValue) return;
                _pauses.Add(new long[] { qpc, long.MaxValue });
            }
        }

        public void Resume(long qpc)
        {
            lock (_gate)
            {
                if (_pauses.Count == 0) return;
                long[] last = _pauses[_pauses.Count - 1];
                if (last[1] == long.MaxValue) last[1] = Math.Max(qpc, last[0]);
            }
        }

        public bool IsPaused
        {
            get { lock (_gate) return _pauses.Count > 0 && _pauses[_pauses.Count - 1][1] == long.MaxValue; }
        }

        // Суммарная пауза в 100 нс (открытая пауза считается до «сейчас»).
        public long PausedDuration100ns
        {
            get
            {
                lock (_gate)
                {
                    long sum = 0;
                    foreach (long[] p in _pauses)
                    {
                        long end = p[1] == long.MaxValue ? Stopwatch.GetTimestamp() : p[1];
                        sum += AudioStream.Qpc100(end) - AudioStream.Qpc100(p[0]);
                    }
                    return sum;
                }
            }
        }

        internal long Origin100 { get { lock (_gate) return _origin100; } }

        // Сдвинуть cursor за паузу и ограничить runEnd началом следующей паузы. end — сколько кадров есть.
        internal void ClipPauses(ref long cursor, ref long runEnd, long end)
        {
            lock (_gate)
            {
                foreach (long[] p in _pauses)
                {
                    long s = AudioTimeline.FloorFrames(AudioStream.Qpc100(p[0]) - _origin100);
                    long e = p[1] == long.MaxValue ? long.MaxValue : AudioTimeline.FloorFrames(AudioStream.Qpc100(p[1]) - _origin100);
                    if (cursor >= s && cursor < e) cursor = e == long.MaxValue ? Math.Max(cursor, end) : e;
                    if (s > cursor && s < runEnd) runEnd = s;
                }
            }
        }

        // Счётчики для журнала и проверок.
        public string Stats()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (AudioStream s in Streams())
            {
                AudioTimeline t = s.Timeline;
                sb.Append(s.Role).Append(": ").Append(s.DeviceName).Append(" [").Append(s.FormatText).Append("] packets=").Append(t.Packets)
                  .Append(" written=").Append(t.Ring.End).Append(" inserted=").Append(t.InsertedFrames)
                  .Append(" dropped=").Append(t.DroppedFrames).Append(" clockFilled=").Append(t.ClockFilledFrames)
                  .Append(" discont=").Append(t.Discontinuities).Append(" lastErr=").Append(t.LastError.ToString("0.0"))
                  .Append(" corr=").Append((t.Correction * 1e6).ToString("0")).Append("ppm lagMs=").Append(t.Packets == 0 ? "n/a" : (t.MinLag100 / 1e4).ToString("0.0") + ".." + (t.MaxLag100 / 1e4).ToString("0.0"))
                  .Append(" reinits=").Append(s.Reinits).Append("; ");
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_gate) { foreach (AudioTrackSource t in _sources) t.MarkDisposed(); }
            if (_notify != null)
            {
                try
                {
                    AudioInterop.RunMta(delegate()
                    {
                        _notifyEnum.UnregisterEndpointNotificationCallback(_notify);
                        AudioInterop.Release(_notifyEnum);
                    });
                }
                catch (Exception ex) { CapLog.Report(ex); }
                _notify = null; _notifyEnum = null;
            }
            if (_sys != null) _sys.Dispose();
            if (_mic != null) _mic.Dispose();
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        internal sealed class DeviceNotifications : AudioInterop.IMMNotificationClient
        {
            private readonly AudioCapture _owner;
            public DeviceNotifications(AudioCapture owner) { _owner = owner; }
            // Колбэки приходят из потоков MMDevAPI: только флаги, без блокировок и вызовов Core Audio.
            public int OnDeviceStateChanged(string id, int newState) { return 0; }
            public int OnDeviceAdded(string id) { return 0; }
            public int OnDeviceRemoved(string id) { return 0; }
            public int OnDefaultDeviceChanged(int flow, int role, string id)
            {
                try { _owner.OnDefaultDeviceChanged(flow, role, id); }
                catch { }
                return 0;
            }
            public int OnPropertyValueChanged(string id, AudioInterop.PropertyKey key) { return 0; }
        }
    }

    // Источник-дорожка: читает одну или две шкалы, применяет громкость, моно, мягкое ограничение и паузы.
    internal sealed class AudioTrackSource : IAudioSource
    {
        private readonly AudioCapture _owner;
        private readonly AudioStream _sys, _mic;
        private readonly float _sysGain, _micGain;
        private readonly bool _mono, _clip;
        public readonly string Name;
        private readonly object _gate = new object();
        private bool _started, _disposed;
        private long _cursor, _offset100, _stopFrame = long.MaxValue;
        private float[] _tmp = new float[0];
        public long LostFrames;

        public AudioTrackSource(AudioCapture owner, string name, AudioStream sys, float sysGain, AudioStream mic, float micGain, bool mono)
        {
            _owner = owner; Name = name; _sys = sys; _mic = mic; _sysGain = sysGain; _micGain = micGain; _mono = mono;
            // Сумма двух источников или усиление могут выйти за ±1 — тогда мягкое ограничение; иначе сигнал не трогаем.
            _clip = (sys != null && mic != null) || sysGain > 1f || micGain > 1f;
        }

        public int SampleRate { get { return AudioTimeline.Rate; } }
        public int Channels { get { return 2; } }

        public void Start(long startQpc)
        {
            long origin100 = _owner.EnsureOrigin(startQpc);
            lock (_gate)
            {
                _offset100 = AudioStream.Qpc100(startQpc) - origin100;
                // Первый кадр — не раньше startQpc (округление вверх).
                long f = AudioTimeline.FloorFrames(_offset100);
                if (AudioTimeline.FramesTo100ns(f) < _offset100) f++;
                _cursor = Math.Max(0, f);
                _stopFrame = long.MaxValue;
                _started = true;
            }
        }

        public void Stop() { Stop(Stopwatch.GetTimestamp()); }

        // Общая метка QPC для всех дорожек даёт им одинаковую длину (AudioCapture.Stop(qpc) делает именно это).
        public void Stop(long stopQpc)
        {
            long stop;
            lock (_gate)
            {
                if (!_started || _stopFrame != long.MaxValue) return;
                stop = Math.Max(_cursor, AudioTimeline.FloorFrames(AudioStream.Qpc100(stopQpc) - _owner.Origin100));
                _stopFrame = stop;
            }
            // Дождаться, пока шкалы дойдут до конца (задержка устройства или заполнение тишиной ≤ HoldBack).
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1000 && !_disposed)
            {
                if (Available() >= stop) break;
                Thread.Sleep(5);
            }
        }

        internal void MarkDisposed() { lock (_gate) _disposed = true; }

        public void Dispose()
        {
            lock (_gate) { if (_started && _stopFrame == long.MaxValue) _stopFrame = _cursor; }
        }

        private long Available()
        {
            long end = long.MaxValue;
            if (_sys != null) end = Math.Min(end, _sys.Timeline.Ring.End);
            if (_mic != null) end = Math.Min(end, _mic.Timeline.Ring.End);
            return end;
        }

        public int Read(float[] buffer, int maxFrames, out long timestamp100ns)
        {
            timestamp100ns = 0;
            if (buffer == null) throw new ArgumentNullException("buffer");
            lock (_gate)
            {
                if (!_started || _disposed || maxFrames <= 0) return 0;
                // Не дальше «сейчас»: у loopback метки пакетов бывают в будущем (время воспроизведения), а дорожки
                // должны останавливаться и вставать на паузу по одной метке QPC.
                long nowFrame = AudioTimeline.FloorFrames(AudioStream.Now100() - _owner.Origin100);
                long end = Math.Min(Math.Min(Available(), _stopFrame), nowFrame);
                long oldest = 0;
                if (_sys != null) oldest = Math.Max(oldest, _sys.Timeline.Ring.Start);
                if (_mic != null) oldest = Math.Max(oldest, _mic.Timeline.Ring.Start);
                if (_cursor < oldest) { LostFrames += oldest - _cursor; _cursor = oldest; }
                long runEnd = end;
                _owner.ClipPauses(ref _cursor, ref runEnd, end);
                if (runEnd <= _cursor) return 0;
                int n = (int)Math.Min(Math.Min(maxFrames, buffer.Length / 2), runEnd - _cursor);
                if (n <= 0) return 0;

                Array.Clear(buffer, 0, n * 2);
                bool ok = true;
                if (_sys != null) ok = _sys.Timeline.Ring.CopyTo(_cursor, buffer, 0, n, _sysGain, true);
                if (ok && _mic != null)
                {
                    if (_mono)
                    {
                        if (_tmp.Length < n * 2) _tmp = new float[n * 2];
                        ok = _mic.Timeline.Ring.CopyTo(_cursor, _tmp, 0, n, _micGain, false);
                        for (int i = 0; ok && i < n; i++)
                        {
                            float m = (_tmp[2 * i] + _tmp[2 * i + 1]) * 0.5f;
                            buffer[2 * i] += m; buffer[2 * i + 1] += m;
                        }
                    }
                    else ok = _mic.Timeline.Ring.CopyTo(_cursor, buffer, 0, n, _micGain, true);
                }
                if (!ok) return 0;   // кадры перезаписаны между проверкой и копированием: следующий вызов сдвинет курсор
                if (_clip) AudioSoftClip.Apply(buffer, 0, n * 2);
                timestamp100ns = AudioTimeline.FramesTo100ns(_cursor) - _offset100;
                _cursor += n;
                return n;
            }
        }
    }

    // Индикатор уровня микрофона для страницы настроек: пик 0..1 с затуханием.
    internal sealed class MicLevelMeter : IDisposable
    {
        private const double DecaySeconds = 0.3;
        private readonly AudioStream _stream;
        private readonly object _gate = new object();
        private float _level;
        private long _at100;

        private MicLevelMeter(string id)
        {
            _stream = new AudioStream(AudioStreamKind.Microphone, id, 0);
            _stream.MeterOnly = true;
            _stream.Observer = OnData;
        }

        public static MicLevelMeter Start(string micDeviceId)
        {
            MicLevelMeter m = new MicLevelMeter(micDeviceId);
            AudioException err = m._stream.StartAndWait(false);
            if (err != null) { m._stream.Dispose(); throw err; }
            return m;
        }

        public string FormatText { get { return _stream.FormatText; } }

        public float Peak
        {
            get
            {
                lock (_gate) return Decayed(AudioStream.Now100());
            }
        }

        private float Decayed(long now100)
        {
            double dt = (now100 - _at100) / (double)AudioTimeline.Ticks;
            if (dt <= 0) return _level;
            return (float)(_level * Math.Exp(-dt / DecaySeconds));
        }

        private void OnData(float[] stereo, int frames)
        {
            float peak = 0f;
            for (int i = 0; i < frames * 2; i++)
            {
                float v = stereo[i];
                if (v < 0) v = -v;
                if (v > peak) peak = v;
            }
            if (peak > 1f) peak = 1f;
            long now = AudioStream.Now100();
            lock (_gate)
            {
                float cur = Decayed(now);
                _level = Math.Max(cur, peak);
                _at100 = now;
            }
        }

        public void Dispose() { _stream.Dispose(); }
    }
}
