// SysDeck — «Захват»: VideoRecorder — запуск, пауза, остановка, сборка конвейера и выбор кодировщика.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using WinCap = Windows.Graphics.Capture;
using WinDx = Windows.Graphics.DirectX;
using WinD3D = Windows.Graphics.DirectX.Direct3D11;

namespace SysDeck.Capture
{
    // ------------------------------------------------------------------ //
    //  Запись
    // ------------------------------------------------------------------ //
    internal sealed partial class VideoRecorder : IDisposable
    {
        private sealed class QItem
        {
            public uint Stream;
            public IntPtr Sample;
            public bool Video;
        }

        private readonly VideoOptions _o;
        private string _codec;
        private readonly List<VidAdapter> _adapters;
        private readonly VidOutput _output;

        private Thread _capThread, _writerThread;
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);
        private Exception _initError;
        private volatile bool _running, _finished, _stopRequested;

        private readonly object _pauseGate = new object();
        private bool _paused;
        private long _pauseStartQpc, _pausedTotal100, _finalElapsed100 = -1;
        private readonly List<long[]> _pauses = new List<long[]>();
        private long _startQpc;
        private static readonly long Freq = Stopwatch.Frequency;

        // конвейер
        private VidDevice _dev;
        private VidBlit _blit;
        private VidSource _source;
        private VidWriter _writer;
        private VidEncoderPlan _plan;
        private readonly List<VidSlot> _slots = new List<VidSlot>();
        private VidSlot _pending, _last;
        private bool _pendingSynthetic;
        private IntPtr _staging, _lastBuffer, _srcCopy, _blackStaging;
        private int _srcCopyW, _srcCopyH;
        private uint _srcCopyFmt;
        private int _outW, _outH;
        private Rectangle _crop;
        private bool _windowMode, _cursorDraw;
        private VidFrameHandler _onFrame;

        // звук
        private readonly List<IAudioSource> _audio = new List<IAudioSource>();
        private readonly List<VidAudioFormat> _audioFmt = new List<VidAudioFormat>();
        private float[][] _audioBuf;
        private short[][] _pcmBuf;
        private bool[] _audioBroken;

        // очередь писателя
        private readonly Queue<QItem> _queue = new Queue<QItem>();
        private readonly object _qGate = new object();
        private int _queuedVideo;
        private bool _writerStop;
        private volatile int _writerHr;
        private string _writerError;
        private readonly ManualResetEvent _writerDone = new ManualResetEvent(false);

        // статистика
        private long _bytes, _framesWritten, _uniqueFrames, _dropped, _noSlot;
        private double _actualFps, _capturedFps;
        private volatile bool _black;
        private bool _blackDone;
        private int _blackChecks;
        private bool _blackAllDark = true;
        private long _lastBlackCheckQpc;
        private string _note = "";
        private string _stopReason;

        public event Action<string> AutoStopped;

        private VideoRecorder(VideoOptions o, string codec, List<VidAdapter> adapters, VidOutput output)
        {
            _o = o; _codec = codec; _adapters = adapters; _output = output;
            _onFrame = OnFrame;
        }

        // ---- открытый интерфейс ----
        public bool Paused { get { lock (_pauseGate) return _paused; } }
        public long BytesWritten { get { return Interlocked.Read(ref _bytes); } }
        public string EncoderName { get { VidWriter w = _writer; return w != null ? w.EncoderName : ""; } }
        public double ActualFps { get { return _actualFps; } }
        public bool BlackFramesDetected { get { VidSource s = _source; return _black || (s != null && s.ProtectedContent); } }

        // Дополнительно (диагностика): путь, счётчики, итоговая причина остановки.
        public string SourceKind { get { VidSource s = _source; return s != null ? s.Kind : ""; } }
        public string EncoderPath { get { VidEncoderPlan p = _plan; return p != null ? p.Describe() : ""; } }
        // true — fragmented MP4: файл читаем после обрыва и чинится Repair; false — обычный MP4 (HEVC), обрыв = потеря файла.
        public bool CrashSafe { get { VidWriter w = _writer; return w != null && w.Fragmented; } }
        public bool EncoderHardware { get { VidWriter w = _writer; return w != null && w.EncoderHardware; } }
        public string Codec { get { return _codec; } }
        public Size OutputSize { get { return new Size(_outW, _outH); } }
        public long FramesWritten { get { return Interlocked.Read(ref _framesWritten); } }
        public long UniqueFrames { get { return Interlocked.Read(ref _uniqueFrames); } }
        public long DroppedFrames { get { return Interlocked.Read(ref _dropped); } }
        public double CapturedFps { get { return _capturedFps; } }
        public string Notes { get { return _note; } }
        public string StopReason { get { return _stopReason; } }
        public bool Finished { get { return _finished; } }

        public TimeSpan Elapsed
        {
            get
            {
                if (!_running && _finalElapsed100 < 0) return TimeSpan.Zero;
                lock (_pauseGate)
                {
                    if (_finalElapsed100 >= 0) return TimeSpan.FromTicks(_finalElapsed100);
                    return TimeSpan.FromTicks(Math.Max(0, ElapsedLocked(Stopwatch.GetTimestamp())));
                }
            }
        }

        private long ElapsedLocked(long now)
        {
            long e = To100(now - _startQpc) - _pausedTotal100;
            if (_paused) e -= To100(now - _pauseStartQpc);
            return e;
        }

        public static long To100(long qpcDelta)
        {
            return qpcDelta / Freq * 10000000L + (qpcDelta % Freq) * 10000000L / Freq;
        }

        public static VideoRecorder Start(VideoOptions o)
        {
            if (o == null) throw new ArgumentNullException("o");
            if (string.IsNullOrEmpty(o.Path)) throw new ArgumentException("The output path is empty.");
            if (o.Fps < 1 || o.Fps > 240) throw new ArgumentException("Frame rate must be between 1 and 240.");
            string codec = (o.Codec ?? "h264").Trim().ToLowerInvariant();
            if (codec == "h265") codec = "hevc";
            if (codec != "h264" && codec != "hevc" && codec != "av1") throw new ArgumentException("Unknown codec: " + o.Codec);
            foreach (IAudioSource a in o.AudioTracks)
            {
                if (a == null) throw new ArgumentException("Audio track is null.");
                if ((a.SampleRate != 44100 && a.SampleRate != 48000) || a.Channels < 1 || a.Channels > 2)
                    throw new ArgumentException("Audio track format " + a.SampleRate + " Hz / " + a.Channels + " ch is not supported by AAC (44100 or 48000 Hz, 1-2 channels).");
            }

            List<VidAdapter> adapters = VidDxgi.Adapters();
            VidOutput output;
            if (o.Window != IntPtr.Zero)
            {
                if (!VidNative.IsWindow(o.Window)) throw new ArgumentException("The window handle is not valid.");
                output = VidDxgi.FindOutputForMonitor(adapters, VidNative.MonitorFromWindow(o.Window, 2));
                if (output == null) throw new ArgumentException("The window is not on any monitor.");
            }
            else
            {
                if (o.Area.Width < 2 || o.Area.Height < 2) throw new ArgumentException("The capture area is empty.");
                output = VidDxgi.FindOutputContaining(adapters, o.Area);
                if (output == null) throw new ArgumentException("The capture area must lie within a single monitor.");
            }

            VideoRecorder r = new VideoRecorder(o, codec, adapters, output);
            r._audio.AddRange(o.AudioTracks);
            r._capThread = new Thread(r.CaptureMain);
            r._capThread.Name = "wpc-video-capture";
            r._capThread.IsBackground = true;
            r._capThread.Priority = ThreadPriority.AboveNormal;
            r._capThread.SetApartmentState(ApartmentState.MTA);
            r._capThread.Start();
            r._ready.WaitOne(60000);
            if (!r._running)
            {
                r._stopRequested = true;
                r._capThread.Join(15000);
                Exception e = r._initError ?? new TimeoutException("Video capture did not start within 60 s.");
                if (e is ArgumentException) throw new ArgumentException(e.Message, e);
                throw new InvalidOperationException(e.Message, e);
            }
            return r;
        }

        public void Pause()
        {
            lock (_pauseGate)
            {
                if (_paused || !_running) return;
                _paused = true;
                _pauseStartQpc = Stopwatch.GetTimestamp();
            }
        }

        public void Resume()
        {
            lock (_pauseGate)
            {
                if (!_paused) return;
                long now = Stopwatch.GetTimestamp();
                long s = To100(_pauseStartQpc - _startQpc), e = To100(now - _startQpc);
                _pauses.Add(new long[] { s, e });
                _pausedTotal100 += e - s;
                _paused = false;
            }
        }

        // Останавливает и закрывает файл (Finalize). Не дольше 10 с; повторный вызов безопасен.
        public void Stop()
        {
            Thread t = _capThread;
            if (t == null) return;
            _stopRequested = true;
            if (Thread.CurrentThread == t || Thread.CurrentThread == _writerThread) return;
            if (!t.Join(10000)) CapLog.Write("video: Stop() timed out after 10 s; the fMP4 file stays playable without finalization");
        }

        public void Dispose() { Stop(); }

        // ---- поток захвата ----
        private void CaptureMain()
        {
            string reason = null;
            bool mf = false, timer = false;
            try
            {
                try { VidNative.SetThreadDpiAwarenessContext(VidNative.DpiPerMonitorV2); } catch (EntryPointNotFoundException) { }
                int hr = VidNative.MFStartup(0x20070, 0);
                if (hr < 0) throw new VideoException("Media Foundation is not available (Windows N without Media Feature Pack?), HRESULT " + VidCom.Hex(hr), hr);
                mf = true;
                InitPipeline();
                WaitFirstFrame();
                if (_stopRequested) return;
                _startQpc = Stopwatch.GetTimestamp();
                for (int i = 0; i < _audio.Count; i++) _audio[i].Start(_startQpc);
                VidNative.timeBeginPeriod(1);
                timer = true;
                _running = true;
                _ready.Set();
                reason = Loop();
            }
            catch (Exception ex)
            {
                if (!_running) _initError = ex;
                else { CapLog.Report(ex); reason = Classify(ex); }
            }
            finally
            {
                if (timer) VidNative.timeEndPeriod(1);
                bool abandoned = false;
                try { abandoned = Shutdown(); }
                catch (Exception ex) { CapLog.Report(ex); }
                if (mf && !abandoned) VidNative.MFShutdown();
                _stopReason = reason ?? (_initError != null ? "init-failed" : "stopped");
                _finished = true;
                _ready.Set();
                if (_running && reason != null)
                {
                    CapLog.Write("video: auto-stop '" + reason + "' " + _o.Path);
                    Action<string> h = AutoStopped;
                    string r = reason;
                    if (h != null)
                        ThreadPool.QueueUserWorkItem(delegate(object state)
                        {
                            try { h(r); } catch (Exception ex) { CapLog.Report(ex); }
                        });
                }
            }
        }

        private static string Classify(Exception ex)
        {
            VideoException ve = ex as VideoException;
            if (ve != null && IsDeviceLost(ve.HResult2)) return "device-lost";
            return "error: " + ex.Message;
        }

        private static bool IsDeviceLost(int hr)
        {
            return hr == unchecked((int)0x887A0005) || hr == unchecked((int)0x887A0006) || hr == unchecked((int)0x887A0007) || hr == unchecked((int)0x887A0020);
        }

        private void AddNote(string s)
        {
            _note = _note.Length == 0 ? s : _note + "; " + s;
        }

        private void InitPipeline()
        {
            _dev = VidDevice.Create(_output.Adapter);
            _blit = VidBlit.TryCreate(_dev);
            AddNote("adapter=" + _output.Adapter.Name + " (" + _output.Adapter.Vendor + ", driver " + _output.Adapter.DriverVersion + ")" +
                    (_dev.VideoSupport ? "" : " no-VIDEO_SUPPORT") + (_blit == null ? " no-gpu-scaler" : ""));

            // Источник: WGC, иначе DDA.
            Rectangle area = _o.Area;
            _windowMode = _o.Window != IntPtr.Zero;
            string wgcWhy = null;
            if (_o.Source != "dda")
            {
                if (VidWgcSource.IsSupported())
                {
                    try
                    {
                        VidWgcSource w = VidWgcSource.Create(_dev, _output.Monitor, _o.Window, _o.Cursor, _o.Fps);
                        _source = w;
                        AddNote("wgc border-off=" + w.BorderDisabled + " cursor-flag=" + w.CursorApplied + " min-interval=" + w.MinIntervalApplied);
                        if (_windowMode)
                        {
                            area = new Rectangle(0, 0, w.ItemWidth, w.ItemHeight);
                        }
                    }
                    catch (Exception ex) { wgcWhy = ex.Message; }
                }
                else wgcWhy = "Windows.Graphics.Capture is not supported on this Windows build";
            }
            else wgcWhy = "forced DDA";
            if (_source == null)
            {
                if (_o.Source == "wgc") throw new VideoException("Windows.Graphics.Capture failed: " + wgcWhy, unchecked((int)0x80004005));
                AddNote("dda (" + wgcWhy + ")");
                if (_windowMode)
                {
                    // Без WGC окно пишется как неподвижная область экрана на его мониторе.
                    VidNative.WinRect wr;
                    VidNative.GetWindowRect(_o.Window, out wr);
                    area = Rectangle.Intersect(Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom), _output.Bounds);
                    _windowMode = false;
                    AddNote("window captured as screen region");
                }
                _source = VidDdaSource.Create(_dev, _output);
                _cursorDraw = _o.Cursor;
            }

            // Геометрия: чётные размеры (NV12), уменьшение до OutputHeight только при наличии GPU-масштаба.
            int srcW, srcH;
            if (_windowMode)
            {
                srcW = area.Width & ~1; srcH = area.Height & ~1;
                if (srcW < 2 || srcH < 2) throw new ArgumentException("The window has no visible area (minimized?).");
            }
            else
            {
                _crop = new Rectangle(area.X - _output.Bounds.X, area.Y - _output.Bounds.Y, area.Width & ~1, area.Height & ~1);
                srcW = _crop.Width; srcH = _crop.Height;
                if (srcW < 2 || srcH < 2) throw new ArgumentException("The capture area is empty.");
            }
            _outW = srcW; _outH = srcH;
            if (_o.OutputHeight > 0 && _o.OutputHeight < srcH)
            {
                if (_blit != null)
                {
                    _outH = Math.Max(2, _o.OutputHeight & ~1);
                    _outW = Math.Max(2, (int)Math.Round(srcW * (double)_outH / srcH) & ~1);
                }
                else AddNote("OutputHeight ignored: no GPU scaler");
            }

            // Звук.
            _audioBuf = new float[_audio.Count][];
            _pcmBuf = new short[_audio.Count][];
            _audioBroken = new bool[_audio.Count];
            for (int i = 0; i < _audio.Count; i++)
            {
                VidAudioFormat af = new VidAudioFormat();
                af.Rate = _audio[i].SampleRate; af.Channels = _audio[i].Channels;
                _audioFmt.Add(af);
                _audioBuf[i] = new float[af.Rate / 5 * af.Channels];
                _pcmBuf[i] = new short[_audioBuf[i].Length];
            }

            ChooseEncoderAndOpen();

            // Текстуры пула и служебные.
            uint misc = _cursorDraw ? 0x200u : 0u;
            for (int i = 0; i < (_plan.GpuInput ? 4 : 2); i++) NewSlot(misc);
            if (!_plan.GpuInput) _staging = _dev.CreateTexture(_outW, _outH, 87, 0, 3, 0x20000, 0);
            _blackStaging = _dev.CreateTexture(32 * 5, 32, 87, 0, 3, 0x20000, 0);

            _writerThread = new Thread(WriterMain);
            _writerThread.Name = "wpc-video-writer";
            _writerThread.IsBackground = true;
            _writerThread.SetApartmentState(ApartmentState.MTA);
            _writerThread.Start();
        }

        private int BitrateKbps(string codec)
        {
            if (_o.BitrateKbps > 0) return _o.BitrateKbps;
            string q = (_o.Quality ?? "optimal").ToLowerInvariant();
            double bpp = q == "low" ? 0.05 : q == "high" ? 0.12 : q == "max" ? 0.18 : 0.08;
            if (codec != "h264") bpp *= 0.65;
            double fps = _o.Fps <= 60 ? _o.Fps : 60 + (_o.Fps - 60) * 0.5;
            double kbps = _outW * (double)_outH * fps * bpp / 1000.0;
            return (int)Math.Max(1000, Math.Min(150000, kbps));
        }

        private List<VidEncoderPlan> BuildPlans(string codec)
        {
            List<VidEncoderPlan> plans = new List<VidEncoderPlan>();
            VidAdapter mon = _dev.Adapter;
            string pref = (_o.Encoder ?? "auto").ToLowerInvariant();
            List<VidAdapter> hw = new List<VidAdapter>();
            foreach (VidAdapter a in _adapters) if (!a.Software) hw.Add(a);
            string[] order = { "nvidia", "amd", "intel" };
            hw.Sort(delegate(VidAdapter x, VidAdapter y)
            {
                int ix = Array.IndexOf(order, x.Vendor), iy = Array.IndexOf(order, y.Vendor);
                if (ix < 0) ix = 9;
                if (iy < 0) iy = 9;
                return ix != iy ? ix.CompareTo(iy) : x.Index.CompareTo(y.Index);
            });
            if (pref != "software")
            {
                if (pref == "nvidia" || pref == "amd" || pref == "intel")
                    foreach (VidAdapter a in hw)
                        if (a.Vendor == pref) AddPlan(plans, codec, a, VidEncoderPlan.SameAdapter(a, mon) && _dev.VideoSupport);
                if (!mon.Software && _dev.VideoSupport) AddPlan(plans, codec, mon, true);
                foreach (VidAdapter a in hw)
                    if (!VidEncoderPlan.SameAdapter(a, mon)) AddPlan(plans, codec, a, false);
                if (!mon.Software) AddPlan(plans, codec, mon, false);
            }
            VidEncoderPlan sw = new VidEncoderPlan();
            sw.Codec = codec;
            plans.Add(sw);
            return plans;
        }

        private static void AddPlan(List<VidEncoderPlan> plans, string codec, VidAdapter a, bool gpu)
        {
            foreach (VidEncoderPlan p in plans)
                if (p.Hardware && p.GpuInput == gpu && VidEncoderPlan.SameAdapter(p.Adapter, a)) return;
            VidEncoderPlan n = new VidEncoderPlan();
            n.Codec = codec; n.Hardware = true; n.GpuInput = gpu; n.Adapter = a;
            plans.Add(n);
        }

        private void ChooseEncoderAndOpen()
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(_o.Path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            StringBuilder why = new StringBuilder();
            string[] codecs = _codec == "h264" ? new string[] { "h264" } : new string[] { _codec, "h264" };
            foreach (string codec in codecs)
            {
                foreach (VidEncoderPlan plan in BuildPlans(codec))
                {
                    if (_stopRequested) throw new OperationCanceledException();
                    string name, vendor, note;
                    bool hw;
                    if (!VideoEncoders.Test(plan, _dev, _outW, _outH, _o.Fps, out name, out vendor, out hw, out note))
                    {
                        why.Append(plan.Describe()).Append(": ").Append(note).Append("; ");
                        continue;
                    }
                    try
                    {
                        _writer = VidWriter.Create(_o.Path, plan, _dev, _outW, _outH, _o.Fps, BitrateKbps(codec), _audioFmt, true);
                        _plan = plan;
                        if (codec != _codec) { AddNote("codec " + _codec + " unavailable, fell back to h264"); _codec = codec; }
                        AddNote("encoder=" + _writer.EncoderName + " via " + plan.Describe() + " (" + note + ") container=" + _writer.Container);
                        return;
                    }
                    catch (Exception ex)
                    {
                        why.Append(plan.Describe()).Append(": open ").Append(ex.Message).Append("; ");
                    }
                }
            }
            throw new VideoException("No working video encoder: " + why, unchecked((int)0xC00D5212));
        }

        private VidSlot NewSlot(uint misc)
        {
            VidSlot s = new VidSlot();
            s.Texture = _dev.CreateTexture(_outW, _outH, 87, 0x28, 0, 0, misc);
            s.Rtv = _dev.CreateRtv(s.Texture);
            _dev.Clear(s.Rtv);
            if (_plan.GpuInput) VidSampleTracker.Register(s);
            _slots.Add(s);
            return s;
        }

        private VidSlot AcquireSlot()
        {
            foreach (VidSlot s in _slots)
                if (s != _last && Thread.VolatileRead(ref s.InUse) <= 0) return s;
            if (_slots.Count < 16) return NewSlot(_cursorDraw ? 0x200u : 0u);
            return null;
        }

        private void WaitFirstFrame()
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (_pending == null && sw.ElapsedMilliseconds < 1500 && !_stopRequested)
            {
                _source.Poll(_onFrame);
                if (_source.Lost) throw new VideoException("Capture source lost: " + _source.LostReason, unchecked((int)0x887A0005));
                if (_pending == null) Thread.Sleep(2);
            }
            if (_pending == null)
            {
                // Кадров нет (окно свёрнуто, экран не меняется в DDA): начинаем с чёрного.
                _pending = AcquireSlot();
                _dev.Clear(_pending.Rtv);
                _pendingSynthetic = true;
                AddNote("no first frame within 1.5 s");
            }
            else AddNote("first frame after " + sw.ElapsedMilliseconds + " ms");
        }

    }
}
