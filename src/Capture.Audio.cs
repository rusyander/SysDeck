// Windows Process Cleaner — «Захват»: звук для записи видео (WASAPI shared, событийные потоки).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Источники: системный loopback устройства вывода, loopback одного процесса с дочерним деревом (Windows 10 2004+),
// микрофон. Каждый поток приводится к 48 кГц стерео float и кладётся на общую шкалу времени QPC: кадр N шкалы —
// это момент origin + N/48000 с. Пропуски (loopback молчит, устройство пропало, DATA_DISCONTINUITY) заполняются
// нулями по часам, медленный уход часов устройства относительно QPC подтягивается долями промилле через ресэмплер.
//
// Семантика меток времени (IAudioSource.Read):
//   · timestamp100ns = (индекс первого кадра на шкале) × 10^7 / 48000 − (startQpc источника − origin) в 100 нс,
//     где origin = startQpc первого вызова Start у любого источника этого AudioCapture. Метка — «настенное» время
//     от startQpc, пауза в неё НЕ вычитается.
//   · Кадры, попавшие в интервал паузы [Pause, Resume), не выдаются вовсе: после паузы метка прыгает вперёд ровно
//     на длительность паузы (±1 кадр). Видеопоток вычитает ту же паузу у себя; для точного совпадения границ есть
//     Pause(qpc)/Resume(qpc) с общей меткой QPC.
//   · Read отдаёт один непрерывный отрезок; вызывать в цикле, пока не вернёт 0. Задержка отдачи ≈ период
//     устройства (10–30 мс), в тишине — до HoldBack (≥100 мс).
//   · Stop() фиксирует конец по QPC «сейчас» и ждёт (≤1 с), пока все потоки дойдут до него; после этого Read
//     дочитывает ровно до конца и возвращает 0. AudioCapture.Stop(qpc) — одна метка для всех дорожек.
//   · Read никогда не отдаёт кадры позже «сейчас». Кадр ставится на шкалу по qpcPosition пакета WASAPI: у системного
//     loopback это время воспроизведения (здесь ≈20 мс в будущем), у микрофона и loopback процесса — время
//     записи (≈17 и ≈10 мс в прошлом). Поэтому в начале записи системный звук начинается с ≈20 мс тишины.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace WindowsProcessCleaner.Capture
{
    // ------------------------------------------------------------------ //
    //  COM-интерфейсы Core Audio (вложены, чтобы не столкнуться с именами других файлов)
    // ------------------------------------------------------------------ //
    internal static class AudioInterop
    {
        public const int ERender = 0, ECapture = 1;
        public const int EConsole = 0;
        public const int DeviceStateActive = 1;
        public const int ClsCtxAll = 0x17;

        public const int StreamFlagsLoopback = 0x00020000;
        public const int StreamFlagsEventCallback = 0x00040000;
        public const int StreamFlagsAutoConvertPcm = unchecked((int)0x80000000);
        public const int StreamFlagsSrcDefaultQuality = 0x08000000;

        public const int BufferFlagsDiscontinuity = 1, BufferFlagsSilent = 2, BufferFlagsTimestampError = 4;

        public const int EAccessDenied = unchecked((int)0x80070005);
        public const int ENotFound = unchecked((int)0x80070490);
        public const int ENotImpl = unchecked((int)0x80004001);
        public const int DeviceInvalidated = unchecked((int)0x88890004);
        public const int DeviceInUse = unchecked((int)0x8889000A);
        public const int UnsupportedFormat = unchecked((int)0x88890008);
        public const int ServiceNotRunning = unchecked((int)0x88890010);
        public const int ResourcesInvalidated = unchecked((int)0x88890026);
        public const int ExclusiveModeNotAllowed = unchecked((int)0x8889000E);

        public static readonly Guid IidAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        public static readonly Guid IidAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        public static readonly Guid SubtypePcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
        public static readonly Guid SubtypeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");

        [StructLayout(LayoutKind.Sequential)]
        public struct PropertyKey
        {
            public Guid FormatId;
            public int PropertyId;
            public PropertyKey(Guid f, int p) { FormatId = f; PropertyId = p; }
        }

        // PROPVARIANT: 8 байт заголовка + объединение из двух указателей (24 байта на x64, 16 на x86).
        [StructLayout(LayoutKind.Sequential)]
        public struct PropVariant
        {
            public ushort Vt;
            public ushort Reserved1, Reserved2, Reserved3;
            public IntPtr Pointer;
            public IntPtr Pointer2;
        }

        public static readonly PropertyKey FriendlyName =
            new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        }

        [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMNotificationClient
        {
            [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int newState);
            [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out int frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out int frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
            [PreserveSig] int ReleaseBuffer(int frames);
            [PreserveSig] int GetNextPacketSize(out int frames);
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        public class MMDeviceEnumeratorCo { }

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IActivateAudioInterfaceAsyncOperation
        {
            [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IActivateAudioInterfaceCompletionHandler
        {
            [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
        }

        [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAgileObject { }

        [StructLayout(LayoutKind.Sequential)]
        public struct OsVersionInfo
        {
            public int Size, Major, Minor, Build, Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string CsdVersion;
        }

        [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int ActivateAudioInterfaceAsync(string deviceInterfacePath, ref Guid riid, IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PropVariant pv);

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        public static extern int RtlGetVersion(ref OsVersionInfo info);

        [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref int taskIndex);

        [DllImport("avrt.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

        public static void Release(object o)
        {
            if (o == null) return;
            try { if (Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
            catch { }
        }

        public static string Hr(int hr) { return "0x" + hr.ToString("X8"); }

        // Выполнить действие в MTA: объекты Core Audio живут там, а вызывать нас могут и из STA-потока окна.
        public static void RunMta(Action action)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA) { action(); return; }
            Exception error = null;
            Thread t = new Thread(delegate()
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join();
            if (error != null) throw new AudioException(error.Message, error);
        }
    }

    // Исключение с читаемым английским текстом и кодом причины для интерфейса.
    internal sealed class AudioException : Exception
    {
        public readonly string Reason;
        public readonly int HResult2;

        public AudioException(string message) : base(message) { Reason = "error"; }
        public AudioException(string message, Exception inner) : base(message, inner)
        {
            AudioException a = inner as AudioException;
            Reason = a != null ? a.Reason : "error";
            HResult2 = a != null ? a.HResult2 : 0;
        }
        public AudioException(string reason, string message, int hr) : base(message) { Reason = reason; HResult2 = hr; }
    }

    // ------------------------------------------------------------------ //
    //  Формат потока и разбор байтов в стерео float
    // ------------------------------------------------------------------ //
    internal sealed class AudioFormat
    {
        public int Channels;
        public int SampleRate;
        public int BlockAlign;
        public int ContainerBits;   // бит на отсчёт в контейнере (8/16/24/32/64)
        public int ValidBits;
        public int ChannelMask;
        public bool IsFloat;

        public static AudioFormat FromPointer(IntPtr wfx)
        {
            if (wfx == IntPtr.Zero) throw new AudioException("Audio device returned no format.");
            AudioFormat f = new AudioFormat();
            int tag = (ushort)Marshal.ReadInt16(wfx, 0);
            f.Channels = (ushort)Marshal.ReadInt16(wfx, 2);
            f.SampleRate = Marshal.ReadInt32(wfx, 4);
            f.BlockAlign = (ushort)Marshal.ReadInt16(wfx, 12);
            int bits = (ushort)Marshal.ReadInt16(wfx, 14);
            f.ValidBits = bits;
            if (tag == 0xFFFE)
            {
                int cb = (ushort)Marshal.ReadInt16(wfx, 16);
                if (cb >= 22)
                {
                    int valid = (ushort)Marshal.ReadInt16(wfx, 18);
                    if (valid > 0) f.ValidBits = valid;
                    f.ChannelMask = Marshal.ReadInt32(wfx, 20);
                    byte[] g = new byte[16];
                    Marshal.Copy(new IntPtr(wfx.ToInt64() + 24), g, 0, 16);
                    Guid sub = new Guid(g);
                    if (sub == AudioInterop.SubtypeFloat) tag = 3;
                    else if (sub == AudioInterop.SubtypePcm) tag = 1;
                    else throw new AudioException("Unsupported audio subformat " + sub + ".");
                }
                else tag = 1;
            }
            if (tag != 1 && tag != 3) throw new AudioException("Unsupported audio format tag 0x" + tag.ToString("X") + ".");
            f.IsFloat = tag == 3;
            f.ContainerBits = f.Channels > 0 ? f.BlockAlign * 8 / f.Channels : bits;
            f.Validate();
            return f;
        }

        public static AudioFormat Create(int rate, int channels, int bits, bool isFloat)
        {
            AudioFormat f = new AudioFormat();
            f.SampleRate = rate; f.Channels = channels; f.ContainerBits = bits; f.ValidBits = bits; f.IsFloat = isFloat;
            f.BlockAlign = channels * bits / 8;
            f.Validate();
            return f;
        }

        private void Validate()
        {
            bool ok = Channels >= 1 && Channels <= 32 && SampleRate >= 4000 && SampleRate <= 768000 && BlockAlign > 0;
            if (IsFloat) ok = ok && (ContainerBits == 32 || ContainerBits == 64);
            else ok = ok && (ContainerBits == 8 || ContainerBits == 16 || ContainerBits == 24 || ContainerBits == 32);
            if (!ok) throw new AudioException("Unsupported audio format: " + Describe() + ".");
        }

        // WAVEFORMATEX (18 байт) в неуправляемой памяти; освобождать Marshal.FreeHGlobal.
        public IntPtr ToPointer()
        {
            IntPtr p = Marshal.AllocHGlobal(18);
            Marshal.WriteInt16(p, 0, (short)(IsFloat ? 3 : 1));
            Marshal.WriteInt16(p, 2, (short)Channels);
            Marshal.WriteInt32(p, 4, SampleRate);
            Marshal.WriteInt32(p, 8, SampleRate * BlockAlign);
            Marshal.WriteInt16(p, 12, (short)BlockAlign);
            Marshal.WriteInt16(p, 14, (short)ContainerBits);
            Marshal.WriteInt16(p, 16, 0);
            return p;
        }

        public string Describe()
        {
            return SampleRate + " Hz, " + Channels + " ch, " + (IsFloat ? "float" : "int") + ContainerBits +
                (ValidBits != ContainerBits ? " (" + ValidBits + " valid)" : "") +
                (ChannelMask != 0 ? ", mask 0x" + ChannelMask.ToString("X") : "");
        }
    }

    // Разбор пакета любого формата в чередующееся стерео float; многоканальный звук сводится по маске каналов.
    internal sealed class AudioDecoder
    {
        private readonly AudioFormat _f;
        private readonly float[] _gainL, _gainR;
        private float[] _samples = new float[0];
        private short[] _i16 = new short[0];
        private int[] _i32 = new int[0];
        private double[] _f64 = new double[0];
        private byte[] _bytes = new byte[0];

        public AudioDecoder(AudioFormat format)
        {
            _f = format;
            _gainL = new float[format.Channels];
            _gainR = new float[format.Channels];
            BuildDownmix(format.Channels, format.ChannelMask, _gainL, _gainR);
        }

        public AudioFormat Format { get { return _f; } }

        // Коэффициенты сведения в стерео. Центр и тылы −3 дБ, LFE отбрасывается (как в обычном ITU-даунмиксе).
        internal static void BuildDownmix(int channels, int mask, float[] gl, float[] gr)
        {
            if (channels == 1) { gl[0] = 1f; gr[0] = 1f; return; }
            if (mask == 0)
            {
                // Маска не задана: типовые раскладки по числу каналов.
                switch (channels)
                {
                    case 2: mask = 0x3; break;
                    case 3: mask = 0x7; break;
                    case 4: mask = 0x33; break;
                    case 5: mask = 0x37; break;
                    case 6: mask = 0x3F; break;
                    case 7: mask = 0x13F; break;
                    case 8: mask = 0x63F; break;
                    default: mask = 0; break;
                }
            }
            const float H = 0.7071f;
            int ch = 0;
            for (int bit = 0; bit < 32 && ch < channels; bit++)
            {
                if ((mask & (1 << bit)) == 0) continue;
                float l = 0f, r = 0f;
                switch (1 << bit)
                {
                    case 0x1: l = 1f; break;              // FL
                    case 0x2: r = 1f; break;              // FR
                    case 0x4: l = H; r = H; break;        // FC
                    case 0x8: break;                      // LFE
                    case 0x10: l = H; break;              // BL
                    case 0x20: r = H; break;              // BR
                    case 0x40: l = 0.85f; r = 0.35f; break; // FLC
                    case 0x80: l = 0.35f; r = 0.85f; break; // FRC
                    case 0x100: l = 0.5f; r = 0.5f; break;  // BC
                    case 0x200: l = H; break;             // SL
                    case 0x400: r = H; break;             // SR
                    default: l = 0.5f; r = 0.5f; break;   // верхние и прочие
                }
                gl[ch] = l; gr[ch] = r; ch++;
            }
            // Каналов больше, чем бит в маске: первые два — лево/право, остальные поровну.
            for (; ch < channels; ch++)
            {
                if (ch == 0) { gl[ch] = 1f; }
                else if (ch == 1) { gr[ch] = 1f; }
                else { gl[ch] = 0.5f; gr[ch] = 0.5f; }
            }
        }

        public void Decode(byte[] data, int frames, float[] stereo)
        {
            GCHandle h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try { Decode(h.AddrOfPinnedObject(), frames, stereo); }
            finally { h.Free(); }
        }

        public void Decode(IntPtr data, int frames, float[] stereo)
        {
            int ch = _f.Channels;
            int n = frames * ch;
            if (_samples.Length < n) _samples = new float[n];
            float[] s = _samples;
            if (_f.IsFloat && _f.ContainerBits == 32)
            {
                Marshal.Copy(data, s, 0, n);
            }
            else if (_f.IsFloat)
            {
                if (_f64.Length < n) _f64 = new double[n];
                Marshal.Copy(data, _f64, 0, n);
                for (int i = 0; i < n; i++) s[i] = (float)_f64[i];
            }
            else if (_f.ContainerBits == 16)
            {
                if (_i16.Length < n) _i16 = new short[n];
                Marshal.Copy(data, _i16, 0, n);
                for (int i = 0; i < n; i++) s[i] = _i16[i] / 32768f;
            }
            else if (_f.ContainerBits == 32)
            {
                if (_i32.Length < n) _i32 = new int[n];
                Marshal.Copy(data, _i32, 0, n);
                for (int i = 0; i < n; i++) s[i] = (float)(_i32[i] / 2147483648.0);
            }
            else if (_f.ContainerBits == 24)
            {
                int nb = n * 3;
                if (_bytes.Length < nb) _bytes = new byte[nb];
                Marshal.Copy(data, _bytes, 0, nb);
                byte[] b = _bytes;
                for (int i = 0, j = 0; i < n; i++, j += 3)
                    s[i] = ((b[j] << 8 | b[j + 1] << 16 | b[j + 2] << 24) >> 8) / 8388608f;
            }
            else
            {
                if (_bytes.Length < n) _bytes = new byte[n];
                Marshal.Copy(data, _bytes, 0, n);
                for (int i = 0; i < n; i++) s[i] = (_bytes[i] - 128) / 128f;
            }
            Mix(s, frames, stereo);
        }

        private void Mix(float[] s, int frames, float[] stereo)
        {
            int ch = _f.Channels;
            if (ch == 1)
            {
                for (int i = 0; i < frames; i++) { float v = s[i]; stereo[2 * i] = v; stereo[2 * i + 1] = v; }
                return;
            }
            if (ch == 2)
            {
                Array.Copy(s, 0, stereo, 0, frames * 2);
                return;
            }
            for (int i = 0; i < frames; i++)
            {
                float l = 0f, r = 0f;
                int b = i * ch;
                for (int c = 0; c < ch; c++) { l += _gainL[c] * s[b + c]; r += _gainR[c] * s[b + c]; }
                stereo[2 * i] = l; stereo[2 * i + 1] = r;
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  Ресэмплер: многофазный windowed-sinc (окно Кайзера), шаг можно подстраивать для слежения за часами
    // ------------------------------------------------------------------ //
    internal sealed class AudioResampler
    {
        public const int Half = 32;          // полудлина ядра, отсчётов входа (64 отвода)
        private const int Taps = Half * 2;
        private const int Phases = 256;

        private readonly int _inRate, _outRate;
        private readonly double _baseStep;
        private double _step;
        private readonly float[] _coef, _delta;
        private float[] _l = new float[8192], _r = new float[8192];
        private int _count;
        private int _ipos;       // целая часть позиции (индекс в буфере)
        private double _frac;    // дробная часть: копится отдельно, чтобы сдвиг буфера не менял округление
        private long _base;
        private double _correction;

        public AudioResampler(int inRate, int outRate)
        {
            _inRate = inRate; _outRate = outRate;
            _baseStep = (double)inRate / outRate;
            _step = _baseStep;
            // Срез чуть ниже меньшей из двух частот Найквиста; в долях частоты входа.
            double fc = 0.5 * Math.Min(1.0, (double)outRate / inRate) * 0.92;
            const double beta = 8.0;
            double i0b = BesselI0(beta);
            _coef = new float[(Phases + 1) * Taps];
            _delta = new float[(Phases + 1) * Taps];
            double[] row = new double[Taps];
            for (int p = 0; p <= Phases; p++)
            {
                double phi = (double)p / Phases;
                double sum = 0;
                for (int k = 0; k < Taps; k++)
                {
                    double t = k - Half + 1 - phi;
                    double x = 2 * fc * t;
                    double sinc = Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                    double u = t / Half;
                    double w = Math.Abs(u) >= 1 ? 0 : BesselI0(beta * Math.Sqrt(1 - u * u)) / i0b;
                    row[k] = sinc * w;
                    sum += row[k];
                }
                for (int k = 0; k < Taps; k++) _coef[p * Taps + k] = (float)(row[k] / sum);
            }
            for (int p = 0; p < Phases; p++)
                for (int k = 0; k < Taps; k++)
                    _delta[p * Taps + k] = _coef[(p + 1) * Taps + k] - _coef[p * Taps + k];
            // Предыстория из нулей: первый выходной кадр совпадает по времени с первым входным.
            _count = Half;
            _ipos = Half;
        }

        public int InRate { get { return _inRate; } }
        public int OutRate { get { return _outRate; } }
        public double Correction { get { return _correction; } }

        // Абсолютный индекс входного отсчёта (дробный), которому соответствует следующий выходной кадр.
        public double NextInputPosition { get { return _base + _ipos + _frac - Half; } }

        // c > 0 — выход «медленнее» (меньше кадров на вход), c < 0 — быстрее.
        public void SetCorrection(double c)
        {
            _correction = c;
            _step = _baseStep * (1.0 + c);
        }

        public void Write(float[] stereo, int offsetFrames, int frames)
        {
            if (_count + frames > _l.Length)
            {
                int cap = _l.Length;
                while (cap < _count + frames) cap *= 2;
                Array.Resize(ref _l, cap);
                Array.Resize(ref _r, cap);
            }
            int src = offsetFrames * 2;
            for (int i = 0; i < frames; i++)
            {
                _l[_count + i] = stereo[src + 2 * i];
                _r[_count + i] = stereo[src + 2 * i + 1];
            }
            _count += frames;
        }

        // Выдать все доступные кадры (не больше maxFrames) в чередующееся стерео.
        public int Read(float[] dst, int maxFrames)
        {
            int produced = 0;
            float[] l = _l, r = _r, coef = _coef, delta = _delta;
            while (produced < maxFrames)
            {
                int i = _ipos;
                if (i + Half >= _count) break;
                double ph = _frac * Phases;
                int p = (int)ph;
                float a = (float)(ph - p);
                int row = p * Taps;
                int start = i - Half + 1;
                float sl = 0f, sr = 0f;
                for (int k = 0; k < Taps; k++)
                {
                    float c = coef[row + k] + a * delta[row + k];
                    sl += c * l[start + k];
                    sr += c * r[start + k];
                }
                dst[2 * produced] = sl;
                dst[2 * produced + 1] = sr;
                produced++;
                _frac += _step;
                int adv = (int)_frac;
                _ipos += adv;
                _frac -= adv;
            }
            int drop = _ipos - Half + 1;
            if (drop > 4096 && drop > _count / 2)
            {
                Array.Copy(l, drop, l, 0, _count - drop);
                Array.Copy(r, drop, r, 0, _count - drop);
                _count -= drop; _ipos -= drop; _base += drop;
            }
            return produced;
        }

        // Сколько выходных кадров даст уже записанный вход (оценка сверху для буфера).
        public int MaxOutput(int moreInputFrames)
        {
            return (int)((_count + moreInputFrames - _ipos - _frac) / Math.Max(_step, 1e-6)) + 2;
        }

        private static double BesselI0(double x)
        {
            double sum = 1, term = 1, q = x * x / 4;
            for (int k = 1; k < 64; k++)
            {
                term *= q / ((double)k * k);
                sum += term;
                if (term < sum * 1e-15) break;
            }
            return sum;
        }
    }

    // Мягкое ограничение: линейно до порога, выше — tanh с непрерывной производной; |y| ≤ 1 всегда.
    internal static class AudioSoftClip
    {
        public const float Knee = 0.8f;

        public static float Apply(float x)
        {
            float a = x < 0 ? -x : x;
            if (a <= Knee) return x;
            if (float.IsNaN(x)) return 0f;
            float y = Knee + (1f - Knee) * (float)Math.Tanh((a - Knee) / (1f - Knee));
            return x < 0 ? -y : y;
        }

        public static void Apply(float[] buf, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
            {
                float v = buf[i];
                if (v > Knee || v < -Knee) buf[i] = Apply(v);
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  Кольцевой буфер шкалы: кадры 48 кГц стерео с абсолютным индексом
    // ------------------------------------------------------------------ //
    internal sealed class AudioRing
    {
        private readonly object _gate = new object();
        private readonly float[] _data;
        private readonly int _cap;
        private long _end;

        public AudioRing(int capacityFrames)
        {
            _cap = capacityFrames;
            _data = new float[capacityFrames * 2];
        }

        public long End { get { lock (_gate) return _end; } }

        // Самый старый кадр, который ещё можно прочитать.
        public long Start { get { lock (_gate) return Math.Max(0, _end - _cap); } }

        public void Reset(long end) { lock (_gate) _end = end; }

        public void Append(float[] src, int offsetFrames, int frames)
        {
            if (frames <= 0) return;
            lock (_gate)
            {
                if (frames > _cap) { offsetFrames += frames - _cap; _end += frames - _cap; frames = _cap; }
                int pos = (int)(_end % _cap);
                int first = Math.Min(frames, _cap - pos);
                Array.Copy(src, offsetFrames * 2, _data, pos * 2, first * 2);
                if (frames > first) Array.Copy(src, (offsetFrames + first) * 2, _data, 0, (frames - first) * 2);
                _end += frames;
            }
        }

        public void AppendZeros(long frames)
        {
            if (frames <= 0) return;
            lock (_gate)
            {
                if (frames > _cap) { _end += frames - _cap; frames = _cap; }
                int n = (int)frames;
                int pos = (int)(_end % _cap);
                int first = Math.Min(n, _cap - pos);
                Array.Clear(_data, pos * 2, first * 2);
                if (n > first) Array.Clear(_data, 0, (n - first) * 2);
                _end += n;
            }
        }

        // Скопировать кадры [from, from+frames) с множителем; false — часть уже перезаписана.
        public bool CopyTo(long from, float[] dst, int dstOffsetFrames, int frames, float gain, bool add)
        {
            lock (_gate)
            {
                if (from < _end - _cap || from + frames > _end || from < 0) return false;
                int pos = (int)(from % _cap);
                int d = dstOffsetFrames * 2;
                for (int i = 0; i < frames; i++)
                {
                    int s = ((pos + i) % _cap) * 2;
                    if (add) { dst[d] += _data[s] * gain; dst[d + 1] += _data[s + 1] * gain; }
                    else { dst[d] = _data[s] * gain; dst[d + 1] = _data[s + 1] * gain; }
                    d += 2;
                }
                return true;
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  Размещение пакетов на шкале QPC: ресэмплинг, заполнение пропусков, слежение за уходом часов
    // ------------------------------------------------------------------ //
    internal sealed class AudioTimeline
    {
        public const int Rate = 48000;
        public const long Ticks = 10000000;                 // 100 нс в секунде
        public const int RingSeconds = 10;
        private const double ToleranceFrames = 960;         // 20 мс: больше — вставка нулей или выброс
        private const double RealignFrames = 240;           // 5 мс: допуск после DATA_DISCONTINUITY
        private const double HardAlignFrames = 2;           // начало шкалы, новый формат, после заполнения по часам
        private const double Kp = 0.1;                      // ПИ-регулятор с критическим затуханием (τ ≈ 20 с)
        private const double Ki = Kp * Kp / 4;
        private const double MaxCorrection = 0.002;         // ±0,2 % (≈3,5 цента, на слух незаметно)
        private double _errInt;
        private bool _controllerArmed;

        private readonly object _gate = new object();
        private readonly AudioRing _ring = new AudioRing(Rate * RingSeconds);
        private AudioResampler _rs;
        private long _inTotal;
        private float[] _out = new float[4096];
        private bool _hasOrigin;
        private long _origin100;
        private bool _realign = true;     // мягкое выравнивание (DATA_DISCONTINUITY): допуск RealignFrames
        private bool _hardAlign = true;   // точное выравнивание (начало шкалы, новый формат, заполнение по часам)
        private double _errEma;
        private long _lastPacket100 = long.MinValue;
        private long _holdBack100 = 1000000;               // 100 мс

        // Счётчики для журнала и проверок.
        public long Packets, InsertedFrames, DroppedFrames, ClockFilledFrames, Discontinuities;
        public double LastError;
        // Задержка «сейчас − метка пакета» в 100 нс; отрицательная — метка в будущем (loopback: время воспроизведения).
        public long MinLag100 = long.MaxValue, MaxLag100 = long.MinValue;

        public AudioRing Ring { get { return _ring; } }
        public long HoldBack100 { get { return _holdBack100; } }
        public double Correction { get { lock (_gate) return _rs == null ? 0 : _rs.Correction; } }

        // Новый входной формат (первое открытие или переинициализация). Коррекция часов переносится.
        public void SetInput(int inRate, long devicePeriod100)
        {
            lock (_gate)
            {
                double keep = _rs != null ? _rs.Correction : 0;
                _rs = new AudioResampler(inRate, Rate);
                _rs.SetCorrection(keep);
                _inTotal = 0;
                _realign = true; _hardAlign = true;
                _holdBack100 = Math.Max(1000000, devicePeriod100 * 3);
            }
        }

        public void SetOrigin(long origin100)
        {
            lock (_gate)
            {
                _origin100 = origin100;
                _hasOrigin = true;
                _ring.Reset(0);
                _realign = true; _hardAlign = true;
                _controllerArmed = false;
                _errEma = 0;
            }
        }

        public bool HasOrigin { get { lock (_gate) return _hasOrigin; } }

        public long FrameAt(long time100)
        {
            lock (_gate) return FloorFrames(time100 - _origin100);
        }

        public static long FloorFrames(long delta100)
        {
            // delta × 48000 / 10^7 = delta × 3 / 625, с округлением вниз и для отрицательных.
            long q = delta100 / 625, r = delta100 % 625;
            if (r < 0) { r += 625; q--; }
            return q * 3 + (r * 3) / 625;
        }

        public static long FramesTo100ns(long frames)
        {
            long q = frames / 3, r = frames % 3;
            return q * 625 + (r * 625) / 3;
        }

        // Пакет: stereo — уже стерео на частоте входа; time100 — QPC (100 нс) первого кадра пакета.
        public void Push(float[] stereo, int frames, long time100, bool discontinuity, long now100)
        {
            lock (_gate)
            {
                if (_rs == null) return;
                Packets++;
                long lag = now100 - time100;
                if (lag < MinLag100) MinLag100 = lag;
                if (lag > MaxLag100) MaxLag100 = lag;
                _lastPacket100 = now100;
                if (discontinuity) { Discontinuities++; _realign = true; }
                double tNext100 = time100 + (_rs.NextInputPosition - _inTotal) * Ticks / _rs.InRate;
                _rs.Write(stereo, 0, frames);
                _inTotal += frames;
                int need = _rs.MaxOutput(0);
                if (_out.Length < need * 2) _out = new float[need * 2 + 1024];
                int n = _rs.Read(_out, need);
                if (!_hasOrigin || n == 0) return;

                double expected = (tNext100 - _origin100) * Rate / (double)Ticks;
                long written = _ring.End;
                double e = expected - written;
                LastError = e;
                int skip = 0;
                // После начала шкалы, заполнения по часам или DATA_DISCONTINUITY допуск узкий (5 мс), иначе 20 мс.
                // Некоторые устройства ставят DATA_DISCONTINUITY на каждый пакет loopback (Realtek USB здесь),
                // поэтому регулятор работает и в режиме выравнивания, а вставка/выброс — только при реальном разрыве.
                double tol = _hardAlign ? HardAlignFrames : _realign ? RealignFrames : ToleranceFrames;
                if (e >= tol)
                {
                    long ins = (long)Math.Round(e);
                    _ring.AppendZeros(ins);
                    InsertedFrames += ins;
                    _errEma = 0;
                }
                else if (e <= -tol)
                {
                    long drop = (long)Math.Round(-e);
                    skip = (int)Math.Min(drop, n);
                    DroppedFrames += skip;
                    _errEma = 0;
                }
                else if (_controllerArmed)
                {
                    // ПИ-регулятор: ошибка в секундах, интеграл убирает постоянный уход часов устройства.
                    _errEma += 0.02 * (e - _errEma);
                    double eSec = _errEma / Rate;
                    double dt = (double)frames / _rs.InRate;
                    _errInt += eSec * dt;
                    double lim = MaxCorrection / Ki;
                    if (_errInt > lim) _errInt = lim;
                    if (_errInt < -lim) _errInt = -lim;
                    double c = -(Kp * eSec + Ki * _errInt);
                    if (c > MaxCorrection) c = MaxCorrection;
                    if (c < -MaxCorrection) c = -MaxCorrection;
                    _rs.SetCorrection(c);
                }
                if (skip < n) _controllerArmed = true;
                // Выброшен весь пакет — точное выравнивание нужно и следующему.
                _realign = skip >= n;
                if (skip < n) _hardAlign = false;
                if (n > skip) _ring.Append(_out, skip, n - skip);
            }
        }

        // Заполнить тишиной по часам, если пакетов нет дольше HoldBack (loopback молчит, устройство пропало).
        public void FillByClock(long now100)
        {
            lock (_gate)
            {
                if (!_hasOrigin) return;
                if (_lastPacket100 != long.MinValue && now100 - _lastPacket100 < _holdBack100) return;
                long target = FloorFrames(now100 - _holdBack100 - _origin100);
                long written = _ring.End;
                if (target > written)
                {
                    _ring.AppendZeros(target - written);
                    ClockFilledFrames += target - written;
                    _realign = true; _hardAlign = true;
                }
            }
        }
    }

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
