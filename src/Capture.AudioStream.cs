// SysDeck — «Захват»: поток WASAPI одного источника и перечень устройств.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace SysDeck.Capture
{
    // ------------------------------------------------------------------ //
    //  Поток захвата одного источника WASAPI (свой MTA-поток, событийный режим)
    // ------------------------------------------------------------------ //
    internal enum AudioStreamKind { SystemLoopback, ProcessLoopback, Microphone }

    internal sealed class AudioStream : IDisposable
    {
        private const long OpenWaitMs = 8000;

        public readonly AudioStreamKind Kind;
        public readonly string DeviceId;         // null — устройство по умолчанию (и следовать за его сменой)
        public readonly int ProcessId;
        public readonly AudioTimeline Timeline = new AudioTimeline();

        // Вызывается из потока захвата: декодированное стерео на частоте входа (для индикатора уровня).
        public volatile Action<float[], int> Observer;
        // Устройство потеряно и за LostGraceMs не вернулось: текст для пользователя (английский).
        public Action<AudioStream, string> LostCallback;

        public const long LostGraceMs = 3000;

        private readonly Thread _thread;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly ManualResetEvent _opened = new ManualResetEvent(false);
        private volatile bool _quit;
        private volatile bool _defaultChanged;
        private AudioException _openError;
        private readonly object _infoGate = new object();
        private string _formatText = "";
        private string _deviceName = "";
        private string _currentId;

        // Состояние WASAPI — только в потоке захвата.
        private AudioInterop.IAudioClient _client;
        private AudioInterop.IAudioCaptureClient _capture;
        private AudioDecoder _decoder;
        private bool _eventMode;
        private float[] _stereo = new float[8192];

        public long Reinits, OpenFailures;
        // Только индикатор уровня: без шкалы и ресэмплинга.
        public volatile bool MeterOnly;

        public AudioStream(AudioStreamKind kind, string deviceId, int processId)
        {
            Kind = kind; DeviceId = deviceId; ProcessId = processId;
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "WPC audio " + kind;
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Priority = ThreadPriority.Highest;
        }

        public string Role
        {
            get { return Kind == AudioStreamKind.Microphone ? "mic" : Kind == AudioStreamKind.ProcessLoopback ? "process" : "system"; }
        }

        public string FormatText { get { lock (_infoGate) return _formatText; } }
        public string DeviceName { get { lock (_infoGate) return _deviceName; } }
        public string CurrentDeviceId { get { lock (_infoGate) return _currentId; } }

        // Запустить поток и дождаться первого открытия. null — открыто; иначе причина (поток продолжает попытки
        // переоткрытия и пишет тишину, если keepRetrying).
        public AudioException StartAndWait(bool keepRetrying)
        {
            _keepRetrying = keepRetrying;
            _thread.Start();
            if (!_opened.WaitOne((int)OpenWaitMs))
                return new AudioException(Role + "-error", "Audio device did not respond within " + (OpenWaitMs / 1000) + " s.", 0);
            return _openError;
        }
        private volatile bool _keepRetrying;

        // Переоткрыть поток (смена устройства по умолчанию или явный запрос интерфейса); шкала продолжается.
        public void RequestReopen()
        {
            _defaultChanged = true;
            _wake.Set();
        }

        public void NotifyDefaultChanged(int flow, string newId)
        {
            if (DeviceId != null && Kind != AudioStreamKind.ProcessLoopback)
                return;
            int myFlow = Kind == AudioStreamKind.Microphone ? AudioInterop.ECapture : AudioInterop.ERender;
            if (Kind == AudioStreamKind.ProcessLoopback || flow != myFlow) return;
            if (string.Equals(newId, CurrentDeviceId, StringComparison.OrdinalIgnoreCase)) return;
            _defaultChanged = true;
            _wake.Set();
        }

        public static long Qpc100(long qpc)
        {
            long f = Stopwatch.Frequency;
            if (f == AudioTimeline.Ticks) return qpc;
            return qpc / f * AudioTimeline.Ticks + qpc % f * AudioTimeline.Ticks / f;
        }

        public static long Now100() { return Qpc100(Stopwatch.GetTimestamp()); }

        private void Run()
        {
            IntPtr mmcss = IntPtr.Zero;
            try { int idx = 0; mmcss = AudioInterop.AvSetMmThreadCharacteristics("Audio", ref idx); }
            catch { }
            bool first = true;
            long lostAt = 0;          // Now100 момента потери; 0 — не потеряно
            bool lostRaised = false;
            long nextAttempt = 0;
            try
            {
                while (!_quit)
                {
                    long now = Now100();
                    if (_client == null && now >= nextAttempt)
                    {
                        try
                        {
                            Open();
                            if (!first)
                            {
                                Reinits++;
                                CapLog.Write("audio: " + Role + " stream re-initialized (" + FormatText + ", " + DeviceName + ")");
                            }
                            lostAt = 0; lostRaised = false;
                        }
                        catch (Exception ex)
                        {
                            Close();
                            OpenFailures++;
                            AudioException ae = Classify(ex);
                            if (first)
                            {
                                _openError = ae;
                                first = false;
                                _opened.Set();
                                if (!_keepRetrying) return;
                                lostAt = now; lostRaised = true;   // о первой неудаче сообщает Create
                            }
                            if (lostAt == 0) lostAt = now;
                            if (!lostRaised && now - lostAt >= LostGraceMs * 10000)
                            {
                                lostRaised = true;
                                RaiseLost(ae.Message);
                            }
                            nextAttempt = now + (lostRaised ? 20000000 : 5000000);
                        }
                        if (first) { first = false; _opened.Set(); }
                    }

                    if (_client == null)
                    {
                        Timeline.FillByClock(Now100());
                        _wake.WaitOne(50);
                        continue;
                    }

                    if (_eventMode) _wake.WaitOne(20); else _wake.WaitOne(10);
                    if (_quit) break;

                    int hr = Drain();
                    if (hr < 0)
                    {
                        string why = "audio: " + Role + " stream failed " + AudioInterop.Hr(hr);
                        CapLog.Write(why);
                        Close();
                        lostAt = Now100(); lostRaised = false;
                        // Bluetooth-гарнитура переключает профиль не мгновенно: первая попытка через 300 мс.
                        nextAttempt = lostAt + 3000000;
                        Timeline.FillByClock(Now100());
                        continue;
                    }
                    Timeline.FillByClock(Now100());

                    if (_defaultChanged)
                    {
                        _defaultChanged = false;
                        CapLog.Write("audio: default " + Role + " device changed, reopening");
                        Close();
                        nextAttempt = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                CapLog.Report(ex);
                if (first) { _openError = Classify(ex); _opened.Set(); }
            }
            finally
            {
                Close();
                if (mmcss != IntPtr.Zero) { try { AudioInterop.AvRevertMmThreadCharacteristics(mmcss); } catch { } }
            }
        }

        private void RaiseLost(string message)
        {
            Action<AudioStream, string> cb = LostCallback;
            if (cb == null) return;
            try { cb(this, message); }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        // Все пакеты, что есть сейчас. Возвращает HRESULT первой ошибки или 0.
        private int Drain()
        {
            int next;
            int hr = _capture.GetNextPacketSize(out next);
            int guard = 0;
            while (hr >= 0 && next > 0 && guard++ < 1000)
            {
                IntPtr data; int frames, flags; long devPos, qpc;
                hr = _capture.GetBuffer(out data, out frames, out flags, out devPos, out qpc);
                if (hr < 0) return hr;
                if (hr == 0x08890001 || frames == 0) { if (hr == 0) _capture.ReleaseBuffer(0); break; }
                long now = Now100();
                if (_stereo.Length < frames * 2) _stereo = new float[frames * 2 + 1024];
                if (MeterOnly && Observer == null) { _capture.ReleaseBuffer(frames); hr = _capture.GetNextPacketSize(out next); continue; }
                try
                {
                    if ((flags & AudioInterop.BufferFlagsSilent) != 0) Array.Clear(_stereo, 0, frames * 2);
                    else _decoder.Decode(data, frames, _stereo);
                }
                finally { _capture.ReleaseBuffer(frames); }
                int rate = _decoder.Format.SampleRate;
                long t = qpc;
                if ((flags & AudioInterop.BufferFlagsTimestampError) != 0 || qpc <= 0)
                    t = now - (long)frames * AudioTimeline.Ticks / rate;
                if (!MeterOnly) Timeline.Push(_stereo, frames, t, (flags & AudioInterop.BufferFlagsDiscontinuity) != 0, now);
                Action<float[], int> obs = Observer;
                if (obs != null) { try { obs(_stereo, frames); } catch (Exception ex) { CapLog.Report(ex); } }
                hr = _capture.GetNextPacketSize(out next);
            }
            return hr < 0 ? hr : 0;
        }

        private void Open()
        {
            Close();
            AudioInterop.IAudioClient client = null;
            IntPtr fmt = IntPtr.Zero;
            bool fmtCoTask = false;
            string name = "", id = null;
            try
            {
                if (Kind == AudioStreamKind.ProcessLoopback)
                {
                    client = ActivateProcessLoopback(ProcessId);
                    name = "process " + ProcessId;
                }
                else
                {
                    client = ActivateEndpoint(out name, out id);
                }

                int baseFlags = Kind == AudioStreamKind.Microphone ? 0 : AudioInterop.StreamFlagsLoopback;
                int hr;
                AudioFormat format;
                bool eventMode = true;
                if (Kind == AudioStreamKind.ProcessLoopback)
                {
                    // У виртуального устройства процесса нет формата микширования: просим float 48 кГц стерео,
                    // запасной — PCM16; преобразование делает Windows (AUTOCONVERTPCM).
                    int convert = AudioInterop.StreamFlagsAutoConvertPcm | AudioInterop.StreamFlagsSrcDefaultQuality;
                    format = AudioFormat.Create(48000, 2, 32, true);
                    fmt = format.ToPointer();
                    hr = client.Initialize(0, baseFlags | convert | AudioInterop.StreamFlagsEventCallback, 2000000, 0, fmt, IntPtr.Zero);
                    if (hr < 0)
                    {
                        Marshal.FreeHGlobal(fmt); fmt = IntPtr.Zero;
                        AudioInterop.Release(client);
                        client = ActivateProcessLoopback(ProcessId);
                        format = AudioFormat.Create(48000, 2, 16, false);
                        fmt = format.ToPointer();
                        hr = client.Initialize(0, baseFlags | convert | AudioInterop.StreamFlagsEventCallback, 2000000, 0, fmt, IntPtr.Zero);
                    }
                }
                else
                {
                    hr = client.GetMixFormat(out fmt);
                    if (hr < 0) throw Fail(hr, "GetMixFormat");
                    fmtCoTask = true;
                    format = AudioFormat.FromPointer(fmt);
                    hr = client.Initialize(0, baseFlags | AudioInterop.StreamFlagsEventCallback, 2000000, 0, fmt, IntPtr.Zero);
                    if (hr < 0 && hr != AudioInterop.EAccessDenied && hr != AudioInterop.DeviceInUse)
                    {
                        // Старые драйверы loopback без событий: повторить опросом на свежем клиенте.
                        AudioInterop.Release(client);
                        string n2, i2;
                        client = ActivateEndpoint(out n2, out i2);
                        eventMode = false;
                        hr = client.Initialize(0, baseFlags, 2000000, 0, fmt, IntPtr.Zero);
                    }
                }
                if (hr < 0) throw Fail(hr, "Initialize");

                long defPeriod, minPeriod;
                if (client.GetDevicePeriod(out defPeriod, out minPeriod) < 0 || defPeriod <= 0) defPeriod = 100000;

                object svc;
                Guid iidCap = AudioInterop.IidAudioCaptureClient;
                hr = client.GetService(ref iidCap, out svc);
                if (hr < 0) throw Fail(hr, "GetService");
                AudioInterop.IAudioCaptureClient capture = (AudioInterop.IAudioCaptureClient)svc;

                if (eventMode)
                {
                    hr = client.SetEventHandle(_wake.SafeWaitHandle.DangerousGetHandle());
                    if (hr < 0) eventMode = false;
                }
                AudioDecoder decoder = new AudioDecoder(format);
                Timeline.SetInput(format.SampleRate, defPeriod);
                hr = client.Start();
                if (hr < 0) { AudioInterop.Release(capture); throw Fail(hr, "Start"); }

                _client = client; client = null;
                _capture = capture;
                _decoder = decoder;
                _eventMode = eventMode;
                lock (_infoGate)
                {
                    _formatText = format.Describe() + (eventMode ? ", event" : ", polled");
                    _deviceName = name;
                    _currentId = id;
                }
            }
            finally
            {
                if (fmt != IntPtr.Zero) { if (fmtCoTask) Marshal.FreeCoTaskMem(fmt); else Marshal.FreeHGlobal(fmt); }
                if (client != null) AudioInterop.Release(client);
            }
        }

        private AudioInterop.IAudioClient ActivateEndpoint(out string name, out string id)
        {
            AudioInterop.IMMDeviceEnumerator en = (AudioInterop.IMMDeviceEnumerator)new AudioInterop.MMDeviceEnumeratorCo();
            AudioInterop.IMMDevice dev = null;
            try
            {
                int flow = Kind == AudioStreamKind.Microphone ? AudioInterop.ECapture : AudioInterop.ERender;
                int hr = DeviceId == null
                    ? en.GetDefaultAudioEndpoint(flow, AudioInterop.EConsole, out dev)
                    : en.GetDevice(DeviceId, out dev);
                if (hr < 0 || dev == null)
                {
                    if (hr == AudioInterop.ENotFound || dev == null)
                        throw new AudioException(Role + "-no-device", DeviceId == null
                            ? (Kind == AudioStreamKind.Microphone ? "No microphone is connected or enabled." : "No audio output device is connected or enabled.")
                            : "The selected audio device is not present.", hr);
                    throw Fail(hr, "GetDevice");
                }
                int state;
                if (dev.GetState(out state) >= 0 && state != AudioInterop.DeviceStateActive)
                    throw new AudioException(Role + "-no-device", "The selected audio device is disabled or unplugged.", 0);
                id = null;
                dev.GetId(out id);
                name = AudioDevices.FriendlyName(dev);
                object o;
                Guid iid = AudioInterop.IidAudioClient;
                hr = dev.Activate(ref iid, AudioInterop.ClsCtxAll, IntPtr.Zero, out o);
                if (hr < 0) throw Fail(hr, "Activate");
                return (AudioInterop.IAudioClient)o;
            }
            finally
            {
                AudioInterop.Release(dev);
                AudioInterop.Release(en);
            }
        }

        private AudioInterop.IAudioClient ActivateProcessLoopback(int pid)
        {
            if (!AudioCapture.ProcessLoopbackSupported)
                throw new AudioException("process-loopback-unsupported",
                    "Per-application audio capture requires Windows 10 version 2004 (build 19041) or newer.", 0);
            try { using (Process.GetProcessById(pid)) { } }
            catch (ArgumentException) { throw new AudioException("process-not-found", "The application to record has exited.", 0); }
            catch (InvalidOperationException) { throw new AudioException("process-not-found", "The application to record has exited.", 0); }

            IntPtr prm = Marshal.AllocHGlobal(12);
            int pvSize = IntPtr.Size == 8 ? 24 : 16;
            IntPtr pv = Marshal.AllocHGlobal(pvSize);
            try
            {
                Marshal.WriteInt32(prm, 0, 1);      // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
                Marshal.WriteInt32(prm, 4, pid);
                Marshal.WriteInt32(prm, 8, 0);      // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
                for (int i = 0; i < pvSize; i++) Marshal.WriteByte(pv, i, 0);
                Marshal.WriteInt16(pv, 0, 65);      // VT_BLOB
                Marshal.WriteInt32(pv, 8, 12);
                Marshal.WriteIntPtr(pv, IntPtr.Size == 8 ? 16 : 12, prm);
                ActivationHandler h = new ActivationHandler();
                AudioInterop.IActivateAudioInterfaceAsyncOperation op;
                Guid iid = AudioInterop.IidAudioClient;
                int hr = AudioInterop.ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pv, h, out op);
                if (hr < 0) throw Fail(hr, "ActivateAudioInterfaceAsync");
                try
                {
                    if (!h.Done.WaitOne(5000)) throw new AudioException("process-error", "Per-application audio capture did not start in time.", 0);
                    if (h.Hr < 0 || h.Client == null) throw Fail(h.Hr, "process loopback activation");
                    return (AudioInterop.IAudioClient)h.Client;
                }
                finally { AudioInterop.Release(op); }
            }
            catch (DllNotFoundException) { throw new AudioException("process-loopback-unsupported", "Per-application audio capture is not available on this Windows.", 0); }
            catch (EntryPointNotFoundException) { throw new AudioException("process-loopback-unsupported", "Per-application audio capture is not available on this Windows.", 0); }
            finally
            {
                Marshal.FreeHGlobal(pv);
                Marshal.FreeHGlobal(prm);
            }
        }

        private AudioException Fail(int hr, string what)
        {
            string reason = Role + "-error";
            string msg;
            if (hr == AudioInterop.EAccessDenied)
            {
                reason = Role + "-access-denied";
                msg = Kind == AudioStreamKind.Microphone
                    ? "Microphone access is turned off in Windows Settings > Privacy > Microphone (allow desktop apps)."
                    : "Windows denied access to the audio device.";
            }
            else if (hr == AudioInterop.DeviceInUse || hr == AudioInterop.ExclusiveModeNotAllowed)
            {
                reason = Role + "-device-in-use";
                msg = "The audio device is used exclusively by another application.";
            }
            else if (hr == AudioInterop.ENotFound || hr == AudioInterop.DeviceInvalidated)
            {
                reason = Role + "-no-device";
                msg = "The audio device is not available (unplugged or disabled).";
            }
            else if (hr == AudioInterop.UnsupportedFormat)
            {
                reason = Role + "-format-unsupported";
                msg = "The audio device format is not supported.";
            }
            else if (hr == AudioInterop.ServiceNotRunning)
            {
                reason = Role + "-no-service";
                msg = "The Windows Audio service is not running.";
            }
            else msg = "Audio " + what + " failed (" + AudioInterop.Hr(hr) + ").";
            return new AudioException(reason, msg, hr);
        }

        private AudioException Classify(Exception ex)
        {
            AudioException ae = ex as AudioException;
            if (ae != null) return ae;
            COMException ce = ex as COMException;
            if (ce != null) return Fail(ce.ErrorCode, "call");
            if (ex is UnauthorizedAccessException) return Fail(AudioInterop.EAccessDenied, "call");
            return new AudioException(Role + "-error", "Audio " + Role + " stream failed: " + ex.Message, 0);
        }

        private void Close()
        {
            if (_client != null) { try { _client.Stop(); } catch { } }
            AudioInterop.Release(_capture);
            AudioInterop.Release(_client);
            _capture = null;
            _client = null;
            _decoder = null;
        }

        public void Dispose()
        {
            _quit = true;
            _wake.Set();
            if (_thread.IsAlive && !_thread.Join(3000)) CapLog.Write("audio: " + Role + " thread did not stop in 3 s");
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        internal sealed class ActivationHandler : AudioInterop.IActivateAudioInterfaceCompletionHandler, AudioInterop.IAgileObject
        {
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public int Hr = -1;
            public object Client;

            public int ActivateCompleted(AudioInterop.IActivateAudioInterfaceAsyncOperation op)
            {
                try
                {
                    int hr; object o;
                    int r = op.GetActivateResult(out hr, out o);
                    Hr = r < 0 ? r : hr;
                    Client = o;
                }
                catch (Exception ex) { Hr = Marshal.GetHRForException(ex); }
                Done.Set();
                return 0;
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  Публичный контракт для видеопотока и настроек
    // ------------------------------------------------------------------ //
    // IAudioSource (контракт с видеозаписью) объявлен в Capture.Video.cs: всегда 48000 Гц, 2 канала.

    internal sealed class AudioDeviceInfo
    {
        public string Id;
        public string Name;
        public bool IsDefault;
    }

    internal static class AudioDevices
    {
        public static List<AudioDeviceInfo> Microphones() { return List(AudioInterop.ECapture); }

        public static List<AudioDeviceInfo> Outputs() { return List(AudioInterop.ERender); }

        private static List<AudioDeviceInfo> List(int flow)
        {
            List<AudioDeviceInfo> result = new List<AudioDeviceInfo>();
            try
            {
                AudioInterop.RunMta(delegate()
                {
                    AudioInterop.IMMDeviceEnumerator en = (AudioInterop.IMMDeviceEnumerator)new AudioInterop.MMDeviceEnumeratorCo();
                    AudioInterop.IMMDeviceCollection col = null;
                    try
                    {
                        string defId = null;
                        AudioInterop.IMMDevice def;
                        if (en.GetDefaultAudioEndpoint(flow, AudioInterop.EConsole, out def) >= 0 && def != null)
                        {
                            def.GetId(out defId);
                            AudioInterop.Release(def);
                        }
                        if (en.EnumAudioEndpoints(flow, AudioInterop.DeviceStateActive, out col) < 0 || col == null) return;
                        int count;
                        if (col.GetCount(out count) < 0) return;
                        for (int i = 0; i < count; i++)
                        {
                            AudioInterop.IMMDevice dev;
                            if (col.Item(i, out dev) < 0 || dev == null) continue;
                            try
                            {
                                AudioDeviceInfo info = new AudioDeviceInfo();
                                dev.GetId(out info.Id);
                                info.Name = FriendlyName(dev);
                                info.IsDefault = info.Id != null && string.Equals(info.Id, defId, StringComparison.OrdinalIgnoreCase);
                                result.Add(info);
                            }
                            finally { AudioInterop.Release(dev); }
                        }
                    }
                    finally
                    {
                        AudioInterop.Release(col);
                        AudioInterop.Release(en);
                    }
                });
            }
            catch (Exception ex)
            {
                // Нет службы Windows Audio или сломан реестр устройств: пустой список, а не падение страницы настроек.
                CapLog.Report(ex);
            }
            return result;
        }

        internal static string FriendlyName(AudioInterop.IMMDevice dev)
        {
            AudioInterop.IPropertyStore store = null;
            try
            {
                if (dev.OpenPropertyStore(0, out store) < 0 || store == null) return "";
                AudioInterop.PropertyKey key = AudioInterop.FriendlyName;
                AudioInterop.PropVariant pv;
                if (store.GetValue(ref key, out pv) < 0) return "";
                string name = pv.Vt == 31 && pv.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(pv.Pointer) : "";
                AudioInterop.PropVariantClear(ref pv);
                return name ?? "";
            }
            catch { return ""; }
            finally { AudioInterop.Release(store); }
        }

        // «Параметры → Конфиденциальность → Микрофон» выключен для всех или для классических приложений.
        public static bool MicrophonePrivacyDenied()
        {
            const string sub = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
            return IsDeny(Registry.CurrentUser, sub) || IsDeny(Registry.CurrentUser, sub + @"\NonPackaged") ||
                   IsDeny(Registry.LocalMachine, sub.Replace("Software", "SOFTWARE"));
        }

        private static bool IsDeny(RegistryKey root, string path)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(path))
                {
                    if (k == null) return false;
                    string v = k.GetValue("Value") as string;
                    return string.Equals(v, "Deny", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }
    }
}
