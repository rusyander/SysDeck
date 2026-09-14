// Windows Process Cleaner — «Захват»: движок записи видео (экран, область, окно) в fMP4.
// Сборка: build.bat (csc.exe из .NET Framework 4.x). Файлу нужны дополнительные ссылки csc:
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.InteropServices.WindowsRuntime.dll
//   %WINDIR%\System32\WinMetadata\Windows.Foundation.winmd
//   %WINDIR%\System32\WinMetadata\Windows.Graphics.winmd
//
// Конвейер: Windows.Graphics.Capture (запасной путь — DXGI Desktop Duplication) на устройстве D3D11 того
// адаптера, к которому подключён монитор → кадрирование/масштаб на GPU в текстуру пула → постоянная частота
// кадров (нет нового кадра — повтор последней текстуры) → IMFSinkWriter с D3D manager и аппаратным MFT →
// контейнер fragmented MP4, который остаётся читаемым даже без Finalize (HEVC приёмник fMP4 не принимает —
// для него обычный MP4, VideoRecorder.CrashSafe = false). Звук: IAudioSource → PCM16 → AAC.
//
// Совместимость: всё, чего нет на старых сборках Windows 10 (рамка WGC, курсор, MinUpdateInterval), включается
// через проверку наличия; нет WGC — DDA; нет аппаратного кодировщика на адаптере монитора — другой адаптер через
// копию в память; нет и его — программный H.264.
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

namespace WindowsProcessCleaner.Capture
{
    // ------------------------------------------------------------------ //
    //  Контракт
    // ------------------------------------------------------------------ //

    // Источник звука (реализует звуковая часть). Рекордер вызывает Start/Stop и по окончании записи Dispose:
    // источники, переданные в VideoOptions.AudioTracks, переходят во владение рекордера.
    internal interface IAudioSource : IDisposable
    {
        int SampleRate { get; }
        int Channels { get; }
        // Кадры (float32, чередование каналов) с прошлого вызова, тишина уже вставлена; timestamp — время первого
        // кадра в единицах 100 нс относительно startQpc из Start. Возвращает число кадров (0 — пока нечего).
        int Read(float[] buffer, int maxFrames, out long timestamp100ns);
        void Start(long startQpc);
        void Stop();
    }

    internal sealed class VideoOptions
    {
        public string Path = null;
        public Rectangle Area;          // физические пиксели виртуального экрана
        public IntPtr Window = IntPtr.Zero; // != 0 → WGC CreateForWindow, кадр вписывается в размер первого кадра
        public int Fps = 60;
        public string Codec = "h264";   // h264 | hevc | av1
        public string Encoder = "auto"; // auto | nvidia | amd | intel | software
        public string Quality = "optimal"; // low | optimal | high | max
        public int BitrateKbps = 0;     // 0 → из качества и размера
        public int OutputHeight = 0;    // 0 → как источник; иначе уменьшение на GPU (только вниз)
        public bool Cursor = true;
        public List<IAudioSource> AudioTracks = new List<IAudioSource>();
        public long MaxBytes = 0;
        public TimeSpan MaxDuration;
        public long MinFreeBytes = 1L << 30;
        public string Source = "auto";  // auto | wgc | dda — для диагностики и тестов
    }

    internal sealed class EncoderInfo
    {
        public string Name;
        public string Vendor;   // nvidia | amd | intel | microsoft | software | VEN_xxxx
        public string Codec;    // h264 | hevc | av1
        public bool Hardware;
        public bool Verified;   // пробное кодирование 10 кадров прошло именно этим MFT
        public string Adapter;  // имя адаптера, на котором проверялся (для аппаратных)
        public string Note;     // причина отказа проверки
    }

    // ------------------------------------------------------------------ //
    //  COM без обёрток: вызовы по номеру слота vtable
    // ------------------------------------------------------------------ //
    internal static class VidCom
    {
        public static T Fn<T>(IntPtr com, int slot) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(com);
            IntPtr fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        public static int QI(IntPtr p, Guid iid, out IntPtr r)
        {
            r = IntPtr.Zero;
            if (p == IntPtr.Zero) return unchecked((int)0x80004003);
            return Marshal.QueryInterface(p, ref iid, out r);
        }

        public static void Rel(ref IntPtr p)
        {
            if (p != IntPtr.Zero) { Marshal.Release(p); p = IntPtr.Zero; }
        }

        public static string Hex(int hr) { return "0x" + hr.ToString("X8", CultureInfo.InvariantCulture); }

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new VideoException(what + " failed, HRESULT " + Hex(hr), hr);
        }

        // ---- делегаты по сигнатурам ----
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DHr(IntPtr s);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DVoid(IntPtr s);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate IntPtr DRetPtr(IntPtr s);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate UIntPtr DRetSize(IntPtr s);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DOutPtr(IntPtr s, out IntPtr o);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DUIntOutPtr(IntPtr s, uint i, out IntPtr o);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DUIntUIntOutPtr(IntPtr s, uint a, uint b, out IntPtr o);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DPtrOutPtr(IntPtr s, IntPtr a, out IntPtr o);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DPtrUInt(IntPtr s, IntPtr p, uint u);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DUIntPtr(IntPtr s, uint i, IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DUIntPtrPtr(IntPtr s, uint i, IntPtr p, IntPtr q);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DUIntInt(IntPtr s, uint i, int v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DPtrOutUInt(IntPtr s, IntPtr p, out uint v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DLong(IntPtr s, long v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DOutLong(IntPtr s, out long v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DUInt(IntPtr s, uint v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DOutUInt(IntPtr s, out uint v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DOutGuid(IntPtr s, out Guid v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DPtr(IntPtr s, IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DPtrPtr(IntPtr s, IntPtr a, IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DLock(IntPtr s, out IntPtr p, out uint max, out uint cur);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DSetU32(IntPtr s, ref Guid k, uint v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DSetU64(IntPtr s, ref Guid k, ulong v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DSetGuid(IntPtr s, ref Guid k, ref Guid v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DSetUnk(IntPtr s, ref Guid k, IntPtr v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DGetStr(IntPtr s, ref Guid k, out IntPtr str, out uint len);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DGetU32(IntPtr s, ref Guid k, out uint v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DGetU64(IntPtr s, ref Guid k, out ulong v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DGetGuid(IntPtr s, ref Guid k, out Guid v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DGetTransform(IntPtr s, uint stream, uint index, out Guid cat, out IntPtr tr);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DReadSample(IntPtr s, uint stream, uint flags, out uint actual, out uint streamFlags, out long ts, out IntPtr sample);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DPresAttr(IntPtr s, uint stream, ref Guid k, IntPtr propVariant);

        // D3D11 / DXGI
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DTexDesc(IntPtr s, out TexDesc d);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DCreateTex(IntPtr s, ref TexDesc d, IntPtr init, out IntPtr tex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DCreateTexInit(IntPtr s, ref TexDesc d, ref SubresData init, out IntPtr tex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DCopySub(IntPtr s, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, ref Box box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DPtrPtrVoid(IntPtr s, IntPtr a, IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DMap(IntPtr s, IntPtr res, uint sub, int type, uint flags, out Mapped m);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DPtrUIntVoid(IntPtr s, IntPtr res, uint sub);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DClearRtv(IntPtr s, IntPtr rtv, [In] float[] color);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DCreateView(IntPtr s, IntPtr res, IntPtr desc, out IntPtr view);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DCreateShader(IntPtr s, IntPtr code, UIntPtr len, IntPtr linkage, out IntPtr shader);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DCreateSampler(IntPtr s, ref SamplerDesc d, out IntPtr sampler);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DCreateBuffer(IntPtr s, ref BufferDesc d, IntPtr init, out IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DUpdateSub(IntPtr s, IntPtr res, uint sub, IntPtr box, IntPtr data, uint rowPitch, uint depthPitch);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DSetArr(IntPtr s, uint start, uint n, ref IntPtr items);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DSetShader(IntPtr s, IntPtr shader, IntPtr classInstances, uint n);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DDraw(IntPtr s, uint count, uint start);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DPtrVoid(IntPtr s, IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DUIntVoid(IntPtr s, uint v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DOmSet(IntPtr s, uint n, ref IntPtr rtv, IntPtr dsv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DViewports(IntPtr s, uint n, ref Viewport vp);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DBlend(IntPtr s, IntPtr blend, IntPtr factor, uint mask);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DDepth(IntPtr s, IntPtr state, uint stencilRef);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DAdapterDesc1(IntPtr s, out AdapterDesc1 d);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DCheckIface(IntPtr s, ref Guid g, out long umdVersion);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DOutputDesc(IntPtr s, out OutputDesc d);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DDup1(IntPtr s, IntPtr dev, uint flags, uint n, [In] uint[] formats, out IntPtr dupl);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DAcquire(IntPtr s, uint timeout, out FrameInfo fi, out IntPtr res);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DDuplDesc(IntPtr s, out DuplDesc d);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DGetDc(IntPtr s, int discard, out IntPtr hdc);

        // ---- структуры ----
        [StructLayout(LayoutKind.Sequential)] public struct TexDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)] public struct SubresData { public IntPtr Data; public uint Pitch, SlicePitch; }
        [StructLayout(LayoutKind.Sequential)] public struct Box { public uint Left, Top, Front, Right, Bottom, Back; }
        [StructLayout(LayoutKind.Sequential)] public struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }
        [StructLayout(LayoutKind.Sequential)] public struct Viewport { public float X, Y, Width, Height, MinDepth, MaxDepth; }
        [StructLayout(LayoutKind.Sequential)]
        public struct SamplerDesc
        {
            public uint Filter, AddressU, AddressV, AddressW; public float MipLodBias; public uint MaxAnisotropy, ComparisonFunc;
            public float B0, B1, B2, B3, MinLod, MaxLod;
        }
        [StructLayout(LayoutKind.Sequential)] public struct BufferDesc { public uint ByteWidth, Usage, BindFlags, CpuAccess, MiscFlags, StructureByteStride; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct AdapterDesc1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public UIntPtr DedicatedVideo, DedicatedSystem, SharedSystem;
            public uint LuidLow; public int LuidHigh; public uint Flags;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct OutputDesc
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public int Left, Top, Right, Bottom; public int Attached; public uint Rotation; public IntPtr Monitor;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct FrameInfo
        {
            public long LastPresentTime, LastMouseUpdateTime; public uint AccumulatedFrames; public int RectsCoalesced, ProtectedContentMaskedOut;
            public int PointerX, PointerY, PointerVisible; public uint TotalMetadataBufferSize, PointerShapeBufferSize;
        }
        [StructLayout(LayoutKind.Sequential)] public struct DuplDesc { public uint Width, Height, RefreshNum, RefreshDen, Format, ScanlineOrdering, Scaling, Rotation; public int DesktopImageInSystemMemory; }
    }

    internal sealed class VideoException : Exception
    {
        public readonly int HResult2;
        public VideoException(string message, int hr) : base(message) { HResult2 = hr; HResult = hr; }
    }

    // ------------------------------------------------------------------ //
    //  Атрибуты Media Foundation и GUID
    // ------------------------------------------------------------------ //
    internal static class MfA
    {
        public static int U32(IntPtr a, Guid k, uint v) { return VidCom.Fn<VidCom.DSetU32>(a, 21)(a, ref k, v); }
        public static int U64(IntPtr a, Guid k, ulong v) { return VidCom.Fn<VidCom.DSetU64>(a, 22)(a, ref k, v); }
        public static int Pair(IntPtr a, Guid k, uint hi, uint lo) { return U64(a, k, ((ulong)hi << 32) | lo); }
        public static int G(IntPtr a, Guid k, Guid v) { return VidCom.Fn<VidCom.DSetGuid>(a, 24)(a, ref k, ref v); }
        public static int Unk(IntPtr a, Guid k, IntPtr v) { return VidCom.Fn<VidCom.DSetUnk>(a, 27)(a, ref k, v); }

        public static string Str(IntPtr a, Guid k)
        {
            IntPtr s; uint len;
            if (a == IntPtr.Zero || VidCom.Fn<VidCom.DGetStr>(a, 13)(a, ref k, out s, out len) != 0) return null;
            string r = Marshal.PtrToStringUni(s);
            Marshal.FreeCoTaskMem(s);
            return r;
        }

        public static bool GetU32(IntPtr a, Guid k, out uint v) { return VidCom.Fn<VidCom.DGetU32>(a, 7)(a, ref k, out v) == 0; }
        public static bool GetU64(IntPtr a, Guid k, out ulong v) { return VidCom.Fn<VidCom.DGetU64>(a, 8)(a, ref k, out v) == 0; }
        public static bool GetGuid(IntPtr a, Guid k, out Guid v) { return VidCom.Fn<VidCom.DGetGuid>(a, 10)(a, ref k, out v) == 0; }

        public static readonly Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid Subtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid AvgBitrate = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static readonly Guid InterlaceMode = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static readonly Guid FrameSize = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid FrameRate = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid PixelAspect = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static readonly Guid DefaultStride = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid AllSamplesIndependent = new Guid("c9173739-5e56-461c-b713-46fb995cb95f");
        public static readonly Guid MaxKeyframeSpacing = new Guid("c16eb52b-73a1-476f-8d62-839d6a020652");
        public static readonly Guid VideoProfile = new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036");
        public static readonly Guid AudioChannels = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        public static readonly Guid AudioSampleRate = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        public static readonly Guid AudioBlockAlign = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
        public static readonly Guid AudioAvgBytes = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        public static readonly Guid AudioBits = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");

        public static readonly Guid MediaVideo = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid MediaAudio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        public static readonly Guid FmtRgb32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
        public static readonly Guid FmtH264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid FmtHevc = new Guid("43564548-0000-0010-8000-00AA00389B71");
        public static readonly Guid FmtAv1 = new Guid("31305641-0000-0010-8000-00AA00389B71");
        public static readonly Guid FmtPcm = new Guid("00000001-0000-0010-8000-00AA00389B71");
        public static readonly Guid FmtAac = new Guid("00001610-0000-0010-8000-00AA00389B71");

        public static readonly Guid ContainerType = new Guid("150ff23f-4abc-478b-ac4f-e1916fba1cca");
        public static readonly Guid ContainerFmp4 = new Guid("9ba876f1-419f-4b77-a1e0-35959d9d4004");
        public static readonly Guid ContainerMp4 = new Guid("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");
        public static readonly Guid EnableHwTransforms = new Guid("a634a91c-822b-41b9-a494-4de4643612b0");
        public static readonly Guid SinkD3DManager = new Guid("ec822da2-e1e9-4b29-a0d8-563c719f5269");
        public static readonly Guid DisableThrottling = new Guid("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
        public static readonly Guid ReaderVideoProcessing = new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        public static readonly Guid PdDuration = new Guid("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
        public static readonly Guid CleanPoint = new Guid("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");

        public static readonly Guid MftFriendlyName = new Guid("314ffbae-5b41-4c95-9c19-4e7d586face3");
        public static readonly Guid MftVendorId = new Guid("3aecb0cc-035b-4bcc-8185-2b8d551ef3af");
        public static readonly Guid MftHardwareUrl = new Guid("2fb866ac-b078-4942-ab6c-003d05cda674");
        public static readonly Guid CatVideoEncoder = new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a");

        public static readonly Guid RateControlMode = new Guid("1c0608e9-370c-4710-8a58-cb6181c42423");
        public static readonly Guid CommonQuality = new Guid("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
        public static readonly Guid MeanBitRate = new Guid("f7222374-2144-4815-b550-a37f8e12ee52");
        public static readonly Guid MaxBitRate = new Guid("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
        public static readonly Guid GopSize = new Guid("95f31b26-95a4-41aa-9303-246a7fc6eef1");
        public static readonly Guid BPictureCount = new Guid("8d390aac-dc5c-4200-b57f-814d04babab2");

        public static readonly Guid IidTexture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        public static readonly Guid IidDxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        public static readonly Guid IidMultithread = new Guid("9b7e4e00-342c-4106-a19f-4f2704f689f0");
        public static readonly Guid IidSinkWriterEx = new Guid("588d72ab-5bc1-496a-8714-b70617141b25");
        public static readonly Guid IidSample = new Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4");
        public static readonly Guid IidTrackedSample = new Guid("245BF8E9-0755-40f7-88A5-AE0F18D55E17");
        public static readonly Guid Iid2DBuffer = new Guid("7DC9D5F9-9ED9-44ec-9BBF-0600BB589FBB");
        public static readonly Guid IidDxgiFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        public static readonly Guid IidOutput1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        public static readonly Guid IidOutput5 = new Guid("80A07424-AB52-42EB-833C-0C42FD282D98");
        public static readonly Guid IidSurface1 = new Guid("4AE63092-6327-4c1b-80AE-BFE12EA32B86");

        public static Guid CodecGuid(string codec)
        {
            if (codec == "hevc") return FmtHevc;
            if (codec == "av1") return FmtAv1;
            return FmtH264;
        }
    }

    internal static class VidNative
    {
        [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll")] public static extern int MFShutdown();
        [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(out IntPtr attrs, uint size);
        [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IntPtr type);
        [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IntPtr sample);
        [DllImport("mfplat.dll")] public static extern int MFCreateTrackedSample(out IntPtr sample);
        [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(uint size, out IntPtr buffer);
        [DllImport("mfplat.dll")] public static extern int MFCreateDXGIDeviceManager(out uint token, out IntPtr manager);
        [DllImport("mfplat.dll")] public static extern int MFCreateDXGISurfaceBuffer(ref Guid riid, IntPtr surface, uint subresource, int bottomUp, out IntPtr buffer);
        [DllImport("mfplat.dll")] public static extern int MFTEnumEx(Guid category, uint flags, IntPtr inputType, ref TypeInfo outputType, out IntPtr activates, out uint count);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] public static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attrs, out IntPtr writer);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] public static extern int MFCreateSourceReaderFromURL(string url, IntPtr attrs, out IntPtr reader);
        [DllImport("ole32.dll")] public static extern int PropVariantClear(IntPtr pv);
        [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr p);
        [DllImport("dxgi.dll")] public static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
        [DllImport("d3d11.dll")] public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint nLevels, uint sdk, out IntPtr device, out int level, out IntPtr context);
        [DllImport("d3d11.dll")] public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr inspectable);
        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
        public static extern int D3DCompile(byte[] src, UIntPtr size, string name, IntPtr defines, IntPtr include, string entry, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetDiskFreeSpaceEx(string dir, out ulong freeForCaller, out ulong total, out ulong totalFree);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetCursorInfo(ref CursorInfo info);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int cx, int cy, uint step, IntPtr brush, uint flags);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] public static extern void CopyMemory(IntPtr dst, IntPtr src, UIntPtr count);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr hwnd, out WinRect rect);

        [StructLayout(LayoutKind.Sequential)] public struct WinRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)] public struct TypeInfo { public Guid Major; public Guid Sub; }
        [StructLayout(LayoutKind.Sequential)] public struct CursorInfo { public int Size; public int Flags; public IntPtr Cursor; public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct IconInfo { public int IsIcon; public int HotX, HotY; public IntPtr Mask, Color; }

        public static readonly IntPtr DpiPerMonitorV2 = new IntPtr(-4);
    }

    // ------------------------------------------------------------------ //
    //  DXGI: адаптеры, выходы, устройство D3D11
    // ------------------------------------------------------------------ //
    internal sealed class VidOutput
    {
        public string DeviceName;
        public Rectangle Bounds;
        public IntPtr Monitor;
        public int OutputIndex;
        public VidAdapter Adapter;
    }

    internal sealed class VidAdapter
    {
        public int Index;
        public string Name;
        public uint VendorId, DeviceId;
        public bool Software;
        public string DriverVersion;
        public List<VidOutput> Outputs = new List<VidOutput>();

        public string Vendor { get { return VidDxgi.VendorName(VendorId); } }

        // Ключ кэша проверки кодировщика: модель GPU + версия драйвера.
        public string Key
        {
            get { return "VEN_" + VendorId.ToString("X4") + "&DEV_" + DeviceId.ToString("X4") + "|" + DriverVersion; }
        }
    }

    internal static class VidDxgi
    {
        public static string VendorName(uint id)
        {
            if (id == 0x10DE) return "nvidia";
            if (id == 0x1002 || id == 0x1022) return "amd";
            if (id == 0x8086 || id == 0x8087) return "intel";
            if (id == 0x1414) return "microsoft";
            if (id == 0x5143) return "qualcomm";
            return "VEN_" + id.ToString("X4");
        }

        public static string VendorFromMft(string venAttr)
        {
            // Атрибут MFT выглядит как «VEN_10DE».
            if (string.IsNullOrEmpty(venAttr)) return null;
            uint id;
            string hex = venAttr.StartsWith("VEN_", StringComparison.OrdinalIgnoreCase) ? venAttr.Substring(4) : venAttr;
            if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)) return VendorName(id);
            return venAttr;
        }

        // Все адаптеры с выходами. Каждый вызов перечисляет заново: мониторы подключают и отключают.
        public static List<VidAdapter> Adapters()
        {
            List<VidAdapter> list = new List<VidAdapter>();
            IntPtr factory;
            Guid iid = MfA.IidDxgiFactory1;
            if (VidNative.CreateDXGIFactory1(ref iid, out factory) < 0) return list;
            try
            {
                for (uint ai = 0; ai < 16; ai++)
                {
                    IntPtr adapter;
                    if (VidCom.Fn<VidCom.DUIntOutPtr>(factory, 12)(factory, ai, out adapter) < 0) break;
                    try
                    {
                        VidCom.AdapterDesc1 ad;
                        if (VidCom.Fn<VidCom.DAdapterDesc1>(adapter, 10)(adapter, out ad) < 0) continue;
                        VidAdapter a = new VidAdapter();
                        a.Index = (int)ai; a.Name = ad.Description; a.VendorId = ad.VendorId; a.DeviceId = ad.DeviceId;
                        a.Software = (ad.Flags & 2) != 0;
                        Guid dxgiDev = MfA.IidDxgiDevice;
                        long umd;
                        if (VidCom.Fn<VidCom.DCheckIface>(adapter, 9)(adapter, ref dxgiDev, out umd) >= 0)
                            a.DriverVersion = ((umd >> 48) & 0xFFFF) + "." + ((umd >> 32) & 0xFFFF) + "." + ((umd >> 16) & 0xFFFF) + "." + (umd & 0xFFFF);
                        else a.DriverVersion = "?";
                        for (uint oi = 0; oi < 16; oi++)
                        {
                            IntPtr output;
                            if (VidCom.Fn<VidCom.DUIntOutPtr>(adapter, 7)(adapter, oi, out output) < 0) break;
                            try
                            {
                                VidCom.OutputDesc od;
                                if (VidCom.Fn<VidCom.DOutputDesc>(output, 7)(output, out od) < 0 || od.Attached == 0) continue;
                                VidOutput o = new VidOutput();
                                o.DeviceName = od.DeviceName; o.Monitor = od.Monitor; o.OutputIndex = (int)oi; o.Adapter = a;
                                o.Bounds = Rectangle.FromLTRB(od.Left, od.Top, od.Right, od.Bottom);
                                a.Outputs.Add(o);
                            }
                            finally { VidCom.Rel(ref output); }
                        }
                        list.Add(a);
                    }
                    finally { VidCom.Rel(ref adapter); }
                }
            }
            finally { VidCom.Rel(ref factory); }
            return list;
        }

        // IDXGIAdapter1 по номеру (AddRef'нутый указатель, освобождает вызывающий).
        public static IntPtr OpenAdapter(int index)
        {
            IntPtr factory;
            Guid iid = MfA.IidDxgiFactory1;
            VidCom.Check(VidNative.CreateDXGIFactory1(ref iid, out factory), "CreateDXGIFactory1");
            try
            {
                IntPtr adapter;
                VidCom.Check(VidCom.Fn<VidCom.DUIntOutPtr>(factory, 12)(factory, (uint)index, out adapter), "EnumAdapters1");
                return adapter;
            }
            finally { VidCom.Rel(ref factory); }
        }

        public static IntPtr OpenOutput(VidOutput o)
        {
            IntPtr adapter = OpenAdapter(o.Adapter.Index);
            try
            {
                IntPtr output;
                VidCom.Check(VidCom.Fn<VidCom.DUIntOutPtr>(adapter, 7)(adapter, (uint)o.OutputIndex, out output), "EnumOutputs");
                return output;
            }
            finally { VidCom.Rel(ref adapter); }
        }

        public static VidOutput FindOutputForMonitor(List<VidAdapter> adapters, IntPtr hmon)
        {
            foreach (VidAdapter a in adapters)
                foreach (VidOutput o in a.Outputs)
                    if (o.Monitor == hmon) return o;
            return null;
        }

        public static VidOutput FindOutputContaining(List<VidAdapter> adapters, Rectangle area)
        {
            foreach (VidAdapter a in adapters)
                foreach (VidOutput o in a.Outputs)
                    if (o.Bounds.Contains(area)) return o;
            return null;
        }
    }

    // Устройство D3D11 на конкретном адаптере с защитой от многопоточности (им же пользуется Media Foundation).
    internal sealed class VidDevice : IDisposable
    {
        public IntPtr Device, Context, Multithread;
        public VidAdapter Adapter;
        public bool VideoSupport;
        public int FeatureLevel;

        private VidCom.DCopySub _copySub;
        private VidCom.DPtrPtrVoid _copyRes;
        private VidCom.DMap _map;
        private VidCom.DPtrUIntVoid _unmap;
        private VidCom.DVoid _enter, _leave;

        public static VidDevice Create(VidAdapter a)
        {
            IntPtr adapter = VidDxgi.OpenAdapter(a.Index);
            try
            {
                IntPtr dev, ctx; int level;
                // BGRA_SUPPORT | VIDEO_SUPPORT; без VIDEO_SUPPORT (старые драйверы, WARP) GPU-путь в MF не годится.
                int hr = VidNative.D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0x20 | 0x800, IntPtr.Zero, 0, 7, out dev, out level, out ctx);
                bool video = hr >= 0;
                if (hr < 0) hr = VidNative.D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out dev, out level, out ctx);
                VidCom.Check(hr, "D3D11CreateDevice on '" + a.Name + "'");
                VidDevice d = new VidDevice();
                d.Device = dev; d.Context = ctx; d.Adapter = a; d.VideoSupport = video; d.FeatureLevel = level;
                if (VidCom.QI(dev, MfA.IidMultithread, out d.Multithread) >= 0)
                    VidCom.Fn<VidCom.DUInt>(d.Multithread, 5)(d.Multithread, 1);
                d._copySub = VidCom.Fn<VidCom.DCopySub>(ctx, 46);
                d._copyRes = VidCom.Fn<VidCom.DPtrPtrVoid>(ctx, 47);
                d._map = VidCom.Fn<VidCom.DMap>(ctx, 14);
                d._unmap = VidCom.Fn<VidCom.DPtrUIntVoid>(ctx, 15);
                if (d.Multithread != IntPtr.Zero)
                {
                    d._enter = VidCom.Fn<VidCom.DVoid>(d.Multithread, 3);
                    d._leave = VidCom.Fn<VidCom.DVoid>(d.Multithread, 4);
                }
                return d;
            }
            finally { VidCom.Rel(ref adapter); }
        }

        public void Enter() { if (_enter != null) _enter(Multithread); }
        public void Leave() { if (_leave != null) _leave(Multithread); }

        public IntPtr CreateTexture(int w, int h, uint format, uint bind, uint usage, uint cpu, uint misc)
        {
            VidCom.TexDesc td = new VidCom.TexDesc();
            td.Width = (uint)w; td.Height = (uint)h; td.MipLevels = 1; td.ArraySize = 1; td.Format = format; td.SampleCount = 1;
            td.Usage = usage; td.BindFlags = bind; td.CpuAccess = cpu; td.MiscFlags = misc;
            IntPtr tex;
            VidCom.Check(VidCom.Fn<VidCom.DCreateTex>(Device, 5)(Device, ref td, IntPtr.Zero, out tex), "CreateTexture2D " + w + "x" + h);
            return tex;
        }

        public IntPtr CreateRtv(IntPtr tex)
        {
            IntPtr v;
            VidCom.Check(VidCom.Fn<VidCom.DCreateView>(Device, 9)(Device, tex, IntPtr.Zero, out v), "CreateRenderTargetView");
            return v;
        }

        public IntPtr CreateSrv(IntPtr tex)
        {
            IntPtr v;
            VidCom.Check(VidCom.Fn<VidCom.DCreateView>(Device, 7)(Device, tex, IntPtr.Zero, out v), "CreateShaderResourceView");
            return v;
        }

        public static VidCom.TexDesc Desc(IntPtr tex)
        {
            VidCom.TexDesc d;
            VidCom.Fn<VidCom.DTexDesc>(tex, 10)(tex, out d);
            return d;
        }

        public void CopyRegion(IntPtr dst, int dx, int dy, IntPtr src, int sx, int sy, int w, int h)
        {
            if (w <= 0 || h <= 0) return;
            VidCom.Box b = new VidCom.Box();
            b.Left = (uint)sx; b.Top = (uint)sy; b.Right = (uint)(sx + w); b.Bottom = (uint)(sy + h); b.Front = 0; b.Back = 1;
            _copySub(Context, dst, 0, (uint)dx, (uint)dy, 0, src, 0, ref b);
        }

        public void Copy(IntPtr dst, IntPtr src) { _copyRes(Context, dst, src); }

        public void Clear(IntPtr rtv)
        {
            VidCom.Fn<VidCom.DClearRtv>(Context, 50)(Context, rtv, new float[] { 0f, 0f, 0f, 1f });
        }

        public int Map(IntPtr res, out VidCom.Mapped m) { return _map(Context, res, 0, 1 /*READ*/, 0, out m); }
        public void Unmap(IntPtr res) { _unmap(Context, res, 0); }

        public int RemovedReason() { return VidCom.Fn<VidCom.DHr>(Device, 39)(Device); }

        public void Dispose()
        {
            VidCom.Rel(ref Multithread);
            VidCom.Rel(ref Context);
            VidCom.Rel(ref Device);
        }
    }

    // Масштаб и перевод FP16 → sRGB на GPU: один полноэкранный треугольник с шейдером, собранным d3dcompiler_47.
    internal sealed class VidBlit : IDisposable
    {
        private const string Hlsl =
            "Texture2D t0 : register(t0); SamplerState s0 : register(s0);\n" +
            "cbuffer cb0 : register(b0) { float4 uvRect; float4 mode; };\n" +
            "struct V { float4 p : SV_Position; float2 uv : TEXCOORD0; };\n" +
            "V vs(uint id : SV_VertexID) { V o; float2 u = float2((id << 1) & 2, id & 2); o.p = float4(u * float2(2, -2) + float2(-1, 1), 0, 1); o.uv = u; return o; }\n" +
            "float4 ps(V i) : SV_Target {\n" +
            "  float3 c = t0.Sample(s0, uvRect.xy + i.uv * uvRect.zw).rgb;\n" +
            "  if (mode.x > 0.5) { c = saturate(c); c = lerp(c * 12.92, 1.055 * pow(c, 1.0 / 2.4) - 0.055, step(0.0031308, c)); }\n" +
            "  return float4(c, 1); }\n";

        private readonly VidDevice _d;
        private IntPtr _vs, _ps, _sampler, _cb;

        private VidBlit(VidDevice d) { _d = d; }

        public static VidBlit TryCreate(VidDevice d)
        {
            if (d.FeatureLevel < 0xa000) return null;
            VidBlit b = new VidBlit(d);
            try
            {
                IntPtr vsCode = Compile("vs", "vs_4_0"), psCode = Compile("ps", "ps_4_0");
                try
                {
                    VidCom.Check(VidCom.Fn<VidCom.DCreateShader>(d.Device, 12)(d.Device, BlobPtr(vsCode), BlobSize(vsCode), IntPtr.Zero, out b._vs), "CreateVertexShader");
                    VidCom.Check(VidCom.Fn<VidCom.DCreateShader>(d.Device, 15)(d.Device, BlobPtr(psCode), BlobSize(psCode), IntPtr.Zero, out b._ps), "CreatePixelShader");
                }
                finally { VidCom.Rel(ref vsCode); VidCom.Rel(ref psCode); }
                VidCom.SamplerDesc sd = new VidCom.SamplerDesc();
                sd.Filter = 0x15; sd.AddressU = 3; sd.AddressV = 3; sd.AddressW = 3; sd.MaxAnisotropy = 1; sd.ComparisonFunc = 1; sd.MaxLod = float.MaxValue;
                VidCom.Check(VidCom.Fn<VidCom.DCreateSampler>(d.Device, 23)(d.Device, ref sd, out b._sampler), "CreateSamplerState");
                VidCom.BufferDesc bd = new VidCom.BufferDesc();
                bd.ByteWidth = 32; bd.Usage = 0; bd.BindFlags = 4;
                VidCom.Check(VidCom.Fn<VidCom.DCreateBuffer>(d.Device, 3)(d.Device, ref bd, IntPtr.Zero, out b._cb), "CreateBuffer(cb)");
                return b;
            }
            catch (Exception ex)
            {
                CapLog.Write("video: GPU scaler unavailable: " + ex.Message);
                b.Dispose();
                return null;
            }
        }

        private static IntPtr Compile(string entry, string target)
        {
            byte[] src = Encoding.ASCII.GetBytes(Hlsl);
            IntPtr code, errors;
            int hr = VidNative.D3DCompile(src, new UIntPtr((uint)src.Length), "wpc-blit", IntPtr.Zero, IntPtr.Zero, entry, target, 1u << 15, 0, out code, out errors);
            string msg = null;
            if (errors != IntPtr.Zero)
            {
                msg = Marshal.PtrToStringAnsi(BlobPtr(errors));
                VidCom.Rel(ref errors);
            }
            if (hr < 0) throw new VideoException("D3DCompile " + entry + ": " + msg, hr);
            return code;
        }

        private static IntPtr BlobPtr(IntPtr blob) { return VidCom.Fn<VidCom.DRetPtr>(blob, 3)(blob); }
        private static UIntPtr BlobSize(IntPtr blob) { return VidCom.Fn<VidCom.DRetSize>(blob, 4)(blob); }

        // src — текстура с BIND_SHADER_RESOURCE; srcRect в пикселях src; dst — RTV текстуры dstW x dstH.
        public void Draw(IntPtr src, int srcW, int srcH, RectangleF srcRect, IntPtr dstRtv, Rectangle dstRect, bool clear, bool linearToSrgb)
        {
            IntPtr srv = _d.CreateSrv(src);
            IntPtr ctx = _d.Context;
            _d.Enter();
            try
            {
                float[] cb = new float[] {
                    srcRect.X / srcW, srcRect.Y / srcH, srcRect.Width / srcW, srcRect.Height / srcH,
                    linearToSrgb ? 1f : 0f, 0f, 0f, 0f };
                GCHandle h = GCHandle.Alloc(cb, GCHandleType.Pinned);
                try { VidCom.Fn<VidCom.DUpdateSub>(ctx, 48)(ctx, _cb, 0, IntPtr.Zero, h.AddrOfPinnedObject(), 0, 0); }
                finally { h.Free(); }

                IntPtr rtv = dstRtv;
                VidCom.Fn<VidCom.DOmSet>(ctx, 33)(ctx, 1, ref rtv, IntPtr.Zero);
                if (clear) _d.Clear(dstRtv);
                VidCom.Viewport vp = new VidCom.Viewport();
                vp.X = dstRect.X; vp.Y = dstRect.Y; vp.Width = dstRect.Width; vp.Height = dstRect.Height; vp.MaxDepth = 1f;
                VidCom.Fn<VidCom.DViewports>(ctx, 44)(ctx, 1, ref vp);
                VidCom.Fn<VidCom.DPtrVoid>(ctx, 43)(ctx, IntPtr.Zero);              // RSSetState
                VidCom.Fn<VidCom.DBlend>(ctx, 35)(ctx, IntPtr.Zero, IntPtr.Zero, 0xFFFFFFFF);
                VidCom.Fn<VidCom.DDepth>(ctx, 36)(ctx, IntPtr.Zero, 0);
                VidCom.Fn<VidCom.DPtrVoid>(ctx, 17)(ctx, IntPtr.Zero);              // IASetInputLayout
                VidCom.Fn<VidCom.DUIntVoid>(ctx, 24)(ctx, 4);                       // TRIANGLELIST
                VidCom.Fn<VidCom.DSetShader>(ctx, 11)(ctx, _vs, IntPtr.Zero, 0);
                VidCom.Fn<VidCom.DSetShader>(ctx, 9)(ctx, _ps, IntPtr.Zero, 0);
                IntPtr s = srv, smp = _sampler, cbp = _cb;
                VidCom.Fn<VidCom.DSetArr>(ctx, 8)(ctx, 0, 1, ref s);
                VidCom.Fn<VidCom.DSetArr>(ctx, 10)(ctx, 0, 1, ref smp);
                VidCom.Fn<VidCom.DSetArr>(ctx, 16)(ctx, 0, 1, ref cbp);
                VidCom.Fn<VidCom.DDraw>(ctx, 13)(ctx, 3, 0);
                IntPtr zero = IntPtr.Zero;
                VidCom.Fn<VidCom.DSetArr>(ctx, 8)(ctx, 0, 1, ref zero);
                zero = IntPtr.Zero;
                VidCom.Fn<VidCom.DOmSet>(ctx, 33)(ctx, 1, ref zero, IntPtr.Zero);
            }
            finally
            {
                _d.Leave();
                VidCom.Rel(ref srv);
            }
        }

        public void Dispose()
        {
            VidCom.Rel(ref _cb); VidCom.Rel(ref _sampler); VidCom.Rel(ref _ps); VidCom.Rel(ref _vs);
        }
    }

    // ------------------------------------------------------------------ //
    //  Источники кадров: Windows.Graphics.Capture и Desktop Duplication
    // ------------------------------------------------------------------ //
    internal delegate void VidFrameHandler(IntPtr texture, int width, int height, uint format);

    internal abstract class VidSource : IDisposable
    {
        public string Kind;
        public volatile bool Lost;          // источник пропал (окно закрыто, монитор отключён, доступ потерян насовсем)
        public volatile bool ProtectedContent;
        public string LostReason;

        // Отдаёт самый свежий кадр, если он появился с прошлого опроса. true — кадр отдан.
        public abstract bool Poll(VidFrameHandler onFrame);
        public abstract void Dispose();
    }

    internal sealed class VidWgcSource : VidSource
    {
        [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IItemInterop
        {
            [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
            [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
        }

        [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDxgiAccess
        {
            [PreserveSig] int GetInterface(ref Guid iid, out IntPtr p);
        }

        private object _d3d;
        private WinCap.GraphicsCaptureItem _item;
        private WinCap.Direct3D11CaptureFramePool _pool;
        private WinCap.GraphicsCaptureSession _session;
        private int _poolW, _poolH;
        public bool BorderDisabled;
        public bool CursorApplied;
        public bool MinIntervalApplied;
        public int ItemWidth, ItemHeight;

        // WGC с интеропом CreateForMonitor/CreateForWindow — Windows 10 1903 (UniversalApiContract 8) и новее.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsSupported()
        {
            try { return ContractPresent() && WinCap.GraphicsCaptureSession.IsSupported(); }
            catch (Exception) { return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ContractPresent()
        {
            return global::Windows.Foundation.Metadata.ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8);
        }

        public static VidWgcSource Create(VidDevice dev, IntPtr monitor, IntPtr window, bool cursor, int fps)
        {
            VidWgcSource s = new VidWgcSource();
            s.Kind = "wgc";
            try
            {
                s.Init(dev, monitor, window, cursor, fps);
                return s;
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Init(VidDevice dev, IntPtr monitor, IntPtr window, bool cursor, int fps)
        {
            IntPtr dxgi, insp;
            VidCom.Check(VidCom.QI(dev.Device, MfA.IidDxgiDevice, out dxgi), "QI IDXGIDevice");
            try { VidCom.Check(VidNative.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out insp), "CreateDirect3D11DeviceFromDXGIDevice"); }
            finally { VidCom.Rel(ref dxgi); }
            try { _d3d = Marshal.GetObjectForIUnknown(insp); }
            finally { VidCom.Rel(ref insp); }

            IItemInterop interop = (IItemInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(WinCap.GraphicsCaptureItem));
            Guid itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            IntPtr itemPtr;
            int hr = window != IntPtr.Zero ? interop.CreateForWindow(window, ref itemIid, out itemPtr) : interop.CreateForMonitor(monitor, ref itemIid, out itemPtr);
            VidCom.Check(hr, window != IntPtr.Zero ? "GraphicsCaptureItem.CreateForWindow" : "GraphicsCaptureItem.CreateForMonitor");
            try { _item = (WinCap.GraphicsCaptureItem)Marshal.GetObjectForIUnknown(itemPtr); }
            finally { VidCom.Rel(ref itemPtr); }
            _item.Closed += (sender, args) => { LostReason = "capture item closed"; Lost = true; };

            ItemWidth = _item.Size.Width; ItemHeight = _item.Size.Height;
            _poolW = Math.Max(1, ItemWidth); _poolH = Math.Max(1, ItemHeight);
            _pool = WinCap.Direct3D11CaptureFramePool.CreateFreeThreaded((WinD3D.IDirect3DDevice)_d3d,
                WinDx.DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, NewSize(_poolW, _poolH));
            _session = _pool.CreateCaptureSession(_item);

            // Свойства новых сборок — через отражение: исходник собирается и против winmd Windows 10.
            BorderDisabled = TrySet("IsBorderRequired", false);
            CursorApplied = TrySet("IsCursorCaptureEnabled", cursor);
            MinIntervalApplied = TrySet("MinUpdateInterval", TimeSpan.FromTicks(10000000L / Math.Max(1, fps)));
            _session.StartCapture();
        }

        private static global::Windows.Graphics.SizeInt32 NewSize(int w, int h)
        {
            global::Windows.Graphics.SizeInt32 sz = new global::Windows.Graphics.SizeInt32();
            sz.Width = w; sz.Height = h;
            return sz;
        }

        private bool TrySet(string property, object value)
        {
            try
            {
                PropertyInfo p = _session.GetType().GetProperty(property);
                if (p == null || !p.CanWrite) return false;
                p.SetValue(_session, value, null);
                object back = p.CanRead ? p.GetValue(_session, null) : value;
                return object.Equals(back, value);
            }
            catch (Exception ex)
            {
                CapLog.Write("video: WGC " + property + " not applied: " + (ex.InnerException ?? ex).Message);
                return false;
            }
        }

        public override bool Poll(VidFrameHandler onFrame)
        {
            WinCap.Direct3D11CaptureFrame latest = null;
            while (true)
            {
                WinCap.Direct3D11CaptureFrame f = _pool.TryGetNextFrame();
                if (f == null) break;
                if (latest != null) latest.Dispose();
                latest = f;
            }
            if (latest == null) return false;
            try
            {
                global::Windows.Graphics.SizeInt32 cs = latest.ContentSize;
                if (cs.Width > 0 && cs.Height > 0 && (cs.Width != _poolW || cs.Height != _poolH))
                {
                    // Окно поменяло размер: пул пересоздаётся под новый размер содержимого.
                    _poolW = cs.Width; _poolH = cs.Height;
                    _pool.Recreate((WinD3D.IDirect3DDevice)_d3d, WinDx.DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, NewSize(_poolW, _poolH));
                }
                object surface = latest.Surface;
                IntPtr tex = IntPtr.Zero;
                try
                {
                    Guid iid = MfA.IidTexture2D;
                    IDxgiAccess acc = (IDxgiAccess)surface;
                    if (acc.GetInterface(ref iid, out tex) < 0 || tex == IntPtr.Zero) return false;
                    VidCom.TexDesc d = VidDevice.Desc(tex);
                    int w = Math.Min(cs.Width > 0 ? cs.Width : (int)d.Width, (int)d.Width);
                    int h = Math.Min(cs.Height > 0 ? cs.Height : (int)d.Height, (int)d.Height);
                    onFrame(tex, w, h, d.Format);
                    return true;
                }
                finally
                {
                    VidCom.Rel(ref tex);
                    if (surface != null && Marshal.IsComObject(surface)) Marshal.ReleaseComObject(surface);
                }
            }
            finally { latest.Dispose(); }
        }

        public override void Dispose()
        {
            try { if (_session != null) _session.Dispose(); } catch (Exception ex) { CapLog.Report(ex); }
            try { if (_pool != null) _pool.Dispose(); } catch (Exception ex) { CapLog.Report(ex); }
            _session = null; _pool = null;
            if (_item != null && Marshal.IsComObject(_item)) Marshal.ReleaseComObject(_item);
            _item = null;
            IDisposable d = _d3d as IDisposable;
            try { if (d != null) d.Dispose(); } catch (Exception ex) { CapLog.Report(ex); }
            _d3d = null;
        }
    }

    internal sealed class VidDdaSource : VidSource
    {
        private const int WaitTimeout = unchecked((int)0x887A0027);
        private const int AccessLost = unchecked((int)0x887A0026);

        private readonly VidDevice _dev;
        private readonly VidOutput _output;
        private IntPtr _dupl;
        private VidCom.DAcquire _acquire;
        private VidCom.DHr _release;
        private bool _first = true;
        private long _nextReopenTicks;
        public uint Format;

        private VidDdaSource(VidDevice dev, VidOutput output) { _dev = dev; _output = output; Kind = "dda"; }

        public static VidDdaSource Create(VidDevice dev, VidOutput output)
        {
            VidDdaSource s = new VidDdaSource(dev, output);
            s.Open();
            return s;
        }

        private void Open()
        {
            // DuplicateOutput1 работает только в потоке с контекстом Per-Monitor v2 (проверено пробой).
            try { VidNative.SetThreadDpiAwarenessContext(VidNative.DpiPerMonitorV2); } catch (EntryPointNotFoundException) { }
            IntPtr output = VidDxgi.OpenOutput(_output);
            try
            {
                IntPtr o5, dupl = IntPtr.Zero;
                int hr;
                if (VidCom.QI(output, MfA.IidOutput5, out o5) >= 0)
                {
                    // Просим только BGRA: на HDR-выходе система переводит сама; если всё же придёт FP16 — переведёт шейдер.
                    try { hr = VidCom.Fn<VidCom.DDup1>(o5, 26)(o5, _dev.Device, 0, 1, new uint[] { 87 }, out dupl); }
                    finally { VidCom.Rel(ref o5); }
                }
                else
                {
                    IntPtr o1;
                    VidCom.Check(VidCom.QI(output, MfA.IidOutput1, out o1), "QI IDXGIOutput1");
                    try { hr = VidCom.Fn<VidCom.DPtrOutPtr>(o1, 22)(o1, _dev.Device, out dupl); }
                    finally { VidCom.Rel(ref o1); }
                }
                if (hr < 0)
                {
                    string why = hr == unchecked((int)0x887A0004) ? " (DXGI_ERROR_UNSUPPORTED: output is driven by another adapter or the driver refuses duplication)" :
                                 hr == unchecked((int)0x887A0022) ? " (DXGI_ERROR_NOT_CURRENTLY_AVAILABLE: too many duplication clients)" :
                                 hr == unchecked((int)0x80070005) ? " (E_ACCESSDENIED: secure desktop or session switch)" : "";
                    throw new VideoException("DuplicateOutput failed, HRESULT " + VidCom.Hex(hr) + why, hr);
                }
                _dupl = dupl;
                VidCom.DuplDesc dd;
                VidCom.Fn<VidCom.DDuplDesc>(_dupl, 7)(_dupl, out dd);
                Format = dd.Format;
                _acquire = VidCom.Fn<VidCom.DAcquire>(_dupl, 8);
                _release = VidCom.Fn<VidCom.DHr>(_dupl, 14);
                _first = true;
            }
            finally { VidCom.Rel(ref output); }
        }

        public override bool Poll(VidFrameHandler onFrame)
        {
            if (_dupl == IntPtr.Zero)
            {
                if (DateTime.UtcNow.Ticks < _nextReopenTicks) return false;
                try { Open(); }
                catch (VideoException ex)
                {
                    _nextReopenTicks = DateTime.UtcNow.Ticks + 2500000;
                    if (ex.HResult2 != unchecked((int)0x80070005) && ex.HResult2 != AccessLost) { LostReason = ex.Message; Lost = true; }
                    return false;
                }
            }
            VidCom.FrameInfo fi;
            IntPtr res;
            int hr = _acquire(_dupl, 0, out fi, out res);
            if (hr == WaitTimeout) return false;
            if (hr < 0)
            {
                // Смена режима, переход на защищённый рабочий стол: пересоздаём дубликат чуть позже.
                VidCom.Rel(ref _dupl);
                _nextReopenTicks = DateTime.UtcNow.Ticks + (hr == AccessLost ? 500000 : 2500000);
                return false;
            }
            IntPtr tex = IntPtr.Zero;
            try
            {
                if (fi.ProtectedContentMaskedOut != 0) ProtectedContent = true;
                bool changed = _first || fi.LastPresentTime != 0 || fi.LastMouseUpdateTime != 0;
                _first = false;
                if (!changed) return false;
                if (VidCom.QI(res, MfA.IidTexture2D, out tex) < 0) return false;
                VidCom.TexDesc d = VidDevice.Desc(tex);
                onFrame(tex, (int)d.Width, (int)d.Height, d.Format);
                return true;
            }
            finally
            {
                VidCom.Rel(ref tex);
                VidCom.Rel(ref res);
                _release(_dupl);
            }
        }

        public override void Dispose() { VidCom.Rel(ref _dupl); }
    }

    // Курсор для пути DDA (дубликат его не содержит): GDI поверх текстуры с флагом GDI_COMPATIBLE.
    internal static class VidCursor
    {
        private static IntPtr _lastCursor;
        private static int _hotX, _hotY;

        public static void Draw(IntPtr texture, int desktopX, int desktopY, double scale, int offsetX, int offsetY)
        {
            VidNative.CursorInfo ci = new VidNative.CursorInfo();
            ci.Size = Marshal.SizeOf(typeof(VidNative.CursorInfo));
            if (!VidNative.GetCursorInfo(ref ci) || (ci.Flags & 1) == 0 || ci.Cursor == IntPtr.Zero) return;
            lock (typeof(VidCursor))
            {
                if (ci.Cursor != _lastCursor)
                {
                    VidNative.IconInfo ii;
                    if (VidNative.GetIconInfo(ci.Cursor, out ii))
                    {
                        _hotX = ii.HotX; _hotY = ii.HotY;
                        if (ii.Mask != IntPtr.Zero) VidNative.DeleteObject(ii.Mask);
                        if (ii.Color != IntPtr.Zero) VidNative.DeleteObject(ii.Color);
                    }
                    _lastCursor = ci.Cursor;
                }
            }
            IntPtr surf;
            if (VidCom.QI(texture, MfA.IidSurface1, out surf) < 0) return;
            try
            {
                IntPtr hdc;
                if (VidCom.Fn<VidCom.DGetDc>(surf, 11)(surf, 0, out hdc) < 0) return;
                try
                {
                    int x = offsetX + (int)Math.Round((ci.X - desktopX) * scale) - _hotX;
                    int y = offsetY + (int)Math.Round((ci.Y - desktopY) * scale) - _hotY;
                    VidNative.DrawIconEx(hdc, x, y, ci.Cursor, 0, 0, 0, IntPtr.Zero, 3 /*DI_NORMAL*/);
                }
                finally { VidCom.Fn<VidCom.DPtr>(surf, 12)(surf, IntPtr.Zero); }
            }
            finally { VidCom.Rel(ref surf); }
        }
    }

    // ------------------------------------------------------------------ //
    //  Слежение за образцами: текстуру пула нельзя перезаписывать, пока её читает кодировщик
    // ------------------------------------------------------------------ //
    // IMFTrackedSample зовёт IMFAsyncCallback, когда Media Foundation отпускает образец. Колбэк собран вручную
    // (vtable в неуправляемой памяти): CCW требует публичных COM-видимых типов, а их в сборке нет.
    internal sealed class VidSlot
    {
        public IntPtr Texture, Rtv;
        public int InUse;
        public int Id;
        public IntPtr Callback;
    }

    internal static class VidSampleTracker
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QiFn(IntPtr self, ref Guid riid, out IntPtr ppv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint RefFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ParamsFn(IntPtr self, IntPtr flags, IntPtr queue);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InvokeFn(IntPtr self, IntPtr result);

        private static readonly object Gate = new object();
        private static readonly Dictionary<int, VidSlot> Slots = new Dictionary<int, VidSlot>();
        private static readonly Guid IidUnknown = new Guid("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IidCallback = new Guid("a27003cf-2354-4f2a-8d6a-ab7cff15437e");
        private static IntPtr _vtable;
        private static int _nextId;
        // Делегаты живут всё время процесса: MF может позвать колбэк и после остановки записи.
        private static QiFn _qi;
        private static RefFn _addRef, _release;
        private static ParamsFn _params;
        private static InvokeFn _invoke;

        private static void EnsureVtable()
        {
            if (_vtable != IntPtr.Zero) return;
            _qi = Qi; _addRef = AddRefImpl; _release = AddRefImpl; _params = GetParameters; _invoke = Invoke;
            IntPtr vt = Marshal.AllocHGlobal(IntPtr.Size * 5);
            Marshal.WriteIntPtr(vt, 0, Marshal.GetFunctionPointerForDelegate(_qi));
            Marshal.WriteIntPtr(vt, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_addRef));
            Marshal.WriteIntPtr(vt, IntPtr.Size * 2, Marshal.GetFunctionPointerForDelegate(_release));
            Marshal.WriteIntPtr(vt, IntPtr.Size * 3, Marshal.GetFunctionPointerForDelegate(_params));
            Marshal.WriteIntPtr(vt, IntPtr.Size * 4, Marshal.GetFunctionPointerForDelegate(_invoke));
            _vtable = vt;
        }

        public static void Register(VidSlot slot)
        {
            lock (Gate)
            {
                EnsureVtable();
                slot.Id = ++_nextId;
                IntPtr obj = Marshal.AllocHGlobal(IntPtr.Size * 2);
                Marshal.WriteIntPtr(obj, 0, _vtable);
                Marshal.WriteIntPtr(obj, IntPtr.Size, new IntPtr(slot.Id));
                slot.Callback = obj;
                Slots[slot.Id] = slot;
            }
        }

        // Объект колбэка освобождается, только если ни один образец его больше не держит.
        public static void Unregister(VidSlot slot)
        {
            lock (Gate)
            {
                Slots.Remove(slot.Id);
                if (slot.Callback != IntPtr.Zero && Thread.VolatileRead(ref slot.InUse) <= 0) Marshal.FreeHGlobal(slot.Callback);
                slot.Callback = IntPtr.Zero;
            }
        }

        // Образец с буфером-текстурой; InUse растёт сейчас и падает, когда MF отпустит образец.
        public static IntPtr CreateSample(VidSlot slot, out int hrOut)
        {
            IntPtr sample = IntPtr.Zero, buffer = IntPtr.Zero, tracked = IntPtr.Zero;
            hrOut = 0;
            try
            {
                Guid iid = MfA.IidTexture2D;
                int hr = VidNative.MFCreateDXGISurfaceBuffer(ref iid, slot.Texture, 0, 0, out buffer);
                if (hr < 0) { hrOut = hr; return IntPtr.Zero; }
                IntPtr b2;
                uint len = 0;
                if (VidCom.QI(buffer, MfA.Iid2DBuffer, out b2) >= 0)
                {
                    VidCom.Fn<VidCom.DOutUInt>(b2, 7)(b2, out len);
                    VidCom.Rel(ref b2);
                }
                VidCom.Fn<VidCom.DUInt>(buffer, 6)(buffer, len);
                hr = VidNative.MFCreateTrackedSample(out tracked);
                if (hr < 0) { hrOut = hr; return IntPtr.Zero; }
                hr = VidCom.QI(tracked, MfA.IidSample, out sample);
                if (hr < 0) { hrOut = hr; return IntPtr.Zero; }
                hr = VidCom.Fn<VidCom.DPtr>(sample, 42)(sample, buffer);
                if (hr < 0) { hrOut = hr; VidCom.Rel(ref sample); return IntPtr.Zero; }
                Interlocked.Increment(ref slot.InUse);
                hr = VidCom.Fn<VidCom.DPtrPtr>(tracked, 3)(tracked, slot.Callback, IntPtr.Zero);
                if (hr < 0) { Interlocked.Decrement(ref slot.InUse); hrOut = hr; VidCom.Rel(ref sample); return IntPtr.Zero; }
                IntPtr r = sample;
                sample = IntPtr.Zero;
                return r;
            }
            finally
            {
                VidCom.Rel(ref sample);
                VidCom.Rel(ref tracked);
                VidCom.Rel(ref buffer);
            }
        }

        private static int Qi(IntPtr self, ref Guid riid, out IntPtr ppv)
        {
            if (riid == IidUnknown || riid == IidCallback) { ppv = self; return 0; }
            ppv = IntPtr.Zero;
            return unchecked((int)0x80004002);
        }

        private static uint AddRefImpl(IntPtr self) { return 1; }

        private static int GetParameters(IntPtr self, IntPtr flags, IntPtr queue) { return unchecked((int)0x80004001); }

        private static int Invoke(IntPtr self, IntPtr result)
        {
            try
            {
                int id = Marshal.ReadIntPtr(self, IntPtr.Size).ToInt32();
                lock (Gate)
                {
                    VidSlot slot;
                    if (Slots.TryGetValue(id, out slot)) Interlocked.Decrement(ref slot.InUse);
                }
            }
            catch { }
            return 0;
        }
    }

    // ------------------------------------------------------------------ //
    //  Sink Writer и выбор кодировщика
    // ------------------------------------------------------------------ //
    internal sealed class VidEncoderPlan
    {
        public string Codec;
        public bool Hardware;
        public bool GpuInput;       // текстуры DXGI прямо в MF (адаптер кодировщика = адаптер захвата)
        public VidAdapter Adapter;  // адаптер кодировщика; null — программный

        public string Describe()
        {
            if (!Hardware) return Codec + "/software/cpu";
            return Codec + "/" + (Adapter != null ? Adapter.Vendor : "?") + "/" + (GpuInput ? "gpu" : "cpu");
        }

        public static bool SameAdapter(VidAdapter a, VidAdapter b)
        {
            return a != null && b != null && a.Index == b.Index && a.Key == b.Key;
        }
    }

    internal sealed class VidAudioFormat
    {
        public int Rate, Channels;
    }

    internal sealed class VidWriter : IDisposable
    {
        public IntPtr Writer;
        public uint VideoStream;
        public uint[] AudioStreams = new uint[0];
        public string EncoderName = "unknown", EncoderVendor;
        public bool EncoderHardware;
        public bool Abandoned;
        private IntPtr _manager;
        private VidDevice _ownDevice;
        private VidCom.DUIntPtr _write;

        public bool Fragmented;
        public string Container = "mp4";

        public static VidWriter Create(string path, VidEncoderPlan plan, VidDevice captureDev, int w, int h, int fps, int kbps,
                                       List<VidAudioFormat> audio, bool fragmented)
        {
            try { return CreateCore(path, plan, captureDev, w, h, fps, kbps, audio, fragmented); }
            catch (VideoException ex)
            {
                // Приёмник fragmented MP4 отказывает HEVC на BeginWriting (MF_E_INVALIDMEDIATYPE) у всех проверенных
                // кодировщиков — и с параметрами кодека (VPS/SPS/PPS) в типе тоже. Тогда пишем обычный MP4:
                // запись работает, но без устойчивости к обрыву (Repair такой файл не спасёт).
                if (!fragmented || plan.Codec == "h264" || ex.HResult2 != unchecked((int)0xC00D36B4)) throw;
            }
            CapLog.Write("video: " + plan.Codec + " refused by fMP4 sink, using plain MP4");
            return CreateCore(path, plan, captureDev, w, h, fps, kbps, audio, false);
        }

        private static VidWriter CreateCore(string path, VidEncoderPlan plan, VidDevice captureDev, int w, int h, int fps, int kbps,
                                            List<VidAudioFormat> audio, bool fragmented)
        {
            VidWriter vw = new VidWriter();
            vw.Fragmented = fragmented;
            vw.Container = fragmented ? "fmp4" : "mp4";
            IntPtr attrs = IntPtr.Zero;
            try
            {
                VidCom.Check(VidNative.MFCreateAttributes(out attrs, 4), "MFCreateAttributes");
                MfA.G(attrs, MfA.ContainerType, fragmented ? MfA.ContainerFmp4 : MfA.ContainerMp4);
                MfA.U32(attrs, MfA.EnableHwTransforms, plan.Hardware ? 1u : 0u);
                VidDevice mgrDev = null;
                if (plan.Hardware && plan.Adapter != null)
                {
                    if (captureDev != null && VidEncoderPlan.SameAdapter(captureDev.Adapter, plan.Adapter)) mgrDev = captureDev;
                    else { vw._ownDevice = VidDevice.Create(plan.Adapter); mgrDev = vw._ownDevice; }
                }
                if (plan.GpuInput && (mgrDev == null || mgrDev != captureDev || !mgrDev.VideoSupport))
                    throw new VideoException("GPU input needs a VIDEO_SUPPORT device on the capture adapter", unchecked((int)0x80070057));
                if (mgrDev != null && mgrDev.VideoSupport)
                {
                    uint token;
                    VidCom.Check(VidNative.MFCreateDXGIDeviceManager(out token, out vw._manager), "MFCreateDXGIDeviceManager");
                    VidCom.Check(VidCom.Fn<VidCom.DPtrUInt>(vw._manager, 7)(vw._manager, mgrDev.Device, token), "IMFDXGIDeviceManager.ResetDevice");
                    MfA.Unk(attrs, MfA.SinkD3DManager, vw._manager);
                }
                VidCom.Check(VidNative.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out vw.Writer), "MFCreateSinkWriterFromURL");

                vw.VideoStream = AddVideo(vw.Writer, plan.Codec, w, h, fps, kbps);
                List<uint> streams = new List<uint>();
                foreach (VidAudioFormat af in audio) streams.Add(AddAudio(vw.Writer, af));
                vw.AudioStreams = streams.ToArray();

                VidCom.Check(VidCom.Fn<VidCom.DHr>(vw.Writer, 5)(vw.Writer), "IMFSinkWriter.BeginWriting");
                vw.ReadEncoder(plan.Codec);
                vw._write = VidCom.Fn<VidCom.DUIntPtr>(vw.Writer, 6);
                return vw;
            }
            catch
            {
                vw.Dispose();
                throw;
            }
            finally { VidCom.Rel(ref attrs); }
        }

        private static uint AddVideo(IntPtr writer, string codec, int w, int h, int fps, int kbps)
        {
            IntPtr outT = IntPtr.Zero, inT = IntPtr.Zero, enc = IntPtr.Zero;
            try
            {
                VidCom.Check(VidNative.MFCreateMediaType(out outT), "MFCreateMediaType");
                MfA.G(outT, MfA.MajorType, MfA.MediaVideo);
                MfA.G(outT, MfA.Subtype, MfA.CodecGuid(codec));
                MfA.U32(outT, MfA.AvgBitrate, (uint)kbps * 1000u);
                MfA.U32(outT, MfA.InterlaceMode, 2);
                MfA.Pair(outT, MfA.FrameSize, (uint)w, (uint)h);
                MfA.Pair(outT, MfA.FrameRate, (uint)fps, 1);
                MfA.Pair(outT, MfA.PixelAspect, 1, 1);
                MfA.U32(outT, MfA.MaxKeyframeSpacing, (uint)(fps * 2));
                if (codec == "h264") MfA.U32(outT, MfA.VideoProfile, 100); // High
                uint idx;
                VidCom.Check(VidCom.Fn<VidCom.DPtrOutUInt>(writer, 3)(writer, outT, out idx), "IMFSinkWriter.AddStream(video " + codec + ")");

                VidCom.Check(VidNative.MFCreateMediaType(out inT), "MFCreateMediaType");
                MfA.G(inT, MfA.MajorType, MfA.MediaVideo);
                MfA.G(inT, MfA.Subtype, MfA.FmtRgb32);
                MfA.U32(inT, MfA.InterlaceMode, 2);
                MfA.Pair(inT, MfA.FrameSize, (uint)w, (uint)h);
                MfA.Pair(inT, MfA.FrameRate, (uint)fps, 1);
                MfA.Pair(inT, MfA.PixelAspect, 1, 1);
                MfA.U32(inT, MfA.DefaultStride, (uint)(w * 4));
                MfA.U32(inT, MfA.AllSamplesIndependent, 1);

                // Параметры кодировщика: VBR со средним битрейтом, GOP = 2 с.
                VidCom.Check(VidNative.MFCreateAttributes(out enc, 4), "MFCreateAttributes");
                MfA.U32(enc, MfA.RateControlMode, 2);
                MfA.U32(enc, MfA.MeanBitRate, (uint)kbps * 1000u);
                MfA.U32(enc, MfA.GopSize, (uint)(fps * 2));
                int hr = VidCom.Fn<VidCom.DUIntPtrPtr>(writer, 4)(writer, idx, inT, enc);
                if (hr < 0) hr = VidCom.Fn<VidCom.DUIntPtrPtr>(writer, 4)(writer, idx, inT, IntPtr.Zero);
                VidCom.Check(hr, "IMFSinkWriter.SetInputMediaType(RGB32 " + w + "x" + h + "@" + fps + ")");
                return idx;
            }
            finally { VidCom.Rel(ref enc); VidCom.Rel(ref inT); VidCom.Rel(ref outT); }
        }

        private static uint AddAudio(IntPtr writer, VidAudioFormat af)
        {
            IntPtr outT = IntPtr.Zero, inT = IntPtr.Zero;
            try
            {
                VidCom.Check(VidNative.MFCreateMediaType(out outT), "MFCreateMediaType");
                MfA.G(outT, MfA.MajorType, MfA.MediaAudio);
                MfA.G(outT, MfA.Subtype, MfA.FmtAac);
                MfA.U32(outT, MfA.AudioBits, 16);
                MfA.U32(outT, MfA.AudioSampleRate, (uint)af.Rate);
                MfA.U32(outT, MfA.AudioChannels, (uint)af.Channels);
                MfA.U32(outT, MfA.AudioAvgBytes, 24000); // 192 кбит/с
                uint idx;
                VidCom.Check(VidCom.Fn<VidCom.DPtrOutUInt>(writer, 3)(writer, outT, out idx), "IMFSinkWriter.AddStream(AAC)");

                VidCom.Check(VidNative.MFCreateMediaType(out inT), "MFCreateMediaType");
                MfA.G(inT, MfA.MajorType, MfA.MediaAudio);
                MfA.G(inT, MfA.Subtype, MfA.FmtPcm);
                MfA.U32(inT, MfA.AudioBits, 16);
                MfA.U32(inT, MfA.AudioSampleRate, (uint)af.Rate);
                MfA.U32(inT, MfA.AudioChannels, (uint)af.Channels);
                MfA.U32(inT, MfA.AudioBlockAlign, (uint)(af.Channels * 2));
                MfA.U32(inT, MfA.AudioAvgBytes, (uint)(af.Rate * af.Channels * 2));
                MfA.U32(inT, MfA.AllSamplesIndependent, 1);
                VidCom.Check(VidCom.Fn<VidCom.DUIntPtrPtr>(writer, 4)(writer, idx, inT, IntPtr.Zero), "IMFSinkWriter.SetInputMediaType(PCM " + af.Rate + "/" + af.Channels + ")");
                return idx;
            }
            finally { VidCom.Rel(ref inT); VidCom.Rel(ref outT); }
        }

        // Какой MFT выбрал Sink Writer: имя, производитель, аппаратный ли.
        private void ReadEncoder(string codec)
        {
            IntPtr ex;
            if (VidCom.QI(Writer, MfA.IidSinkWriterEx, out ex) < 0) return;
            try
            {
                VidCom.DGetTransform get = VidCom.Fn<VidCom.DGetTransform>(ex, 14);
                for (uint i = 0; i < 8; i++)
                {
                    Guid cat;
                    IntPtr tr;
                    if (get(ex, VideoStream, i, out cat, out tr) < 0) break;
                    try
                    {
                        if (cat != MfA.CatVideoEncoder) continue;
                        IntPtr a;
                        string friendly = null, ven = null, url = null;
                        if (VidCom.Fn<VidCom.DOutPtr>(tr, 8)(tr, out a) >= 0 && a != IntPtr.Zero)
                        {
                            friendly = MfA.Str(a, MfA.MftFriendlyName);
                            ven = MfA.Str(a, MfA.MftVendorId);
                            url = MfA.Str(a, MfA.MftHardwareUrl);
                            VidCom.Rel(ref a);
                        }
                        EncoderHardware = ven != null || url != null;
                        EncoderVendor = EncoderHardware ? VidDxgi.VendorFromMft(ven) : "software";
                        string c = codec == "hevc" ? "HEVC" : codec == "av1" ? "AV1" : "H.264";
                        if (!string.IsNullOrEmpty(friendly)) EncoderName = friendly;
                        else if (EncoderHardware) EncoderName = VidEncoderNames.Vendor(EncoderVendor) + " " + c + " Encoder MFT";
                        else EncoderName = "Microsoft " + c + " Encoder MFT (software)";
                    }
                    finally { VidCom.Rel(ref tr); }
                }
            }
            finally { VidCom.Rel(ref ex); }
        }

        public int Write(uint stream, IntPtr sample) { return _write(Writer, stream, sample); }

        public int FinalizeWriter() { return VidCom.Fn<VidCom.DHr>(Writer, 11)(Writer); }

        public void Dispose()
        {
            if (!Abandoned) VidCom.Rel(ref Writer);
            VidCom.Rel(ref _manager);
            if (_ownDevice != null && !Abandoned) { _ownDevice.Dispose(); _ownDevice = null; }
        }
    }

    internal static class VidEncoderNames
    {
        public static string Vendor(string v)
        {
            if (v == "nvidia") return "NVIDIA";
            if (v == "amd") return "AMD";
            if (v == "intel") return "Intel";
            if (v == "microsoft") return "Microsoft";
            if (v == "qualcomm") return "Qualcomm";
            return v ?? "?";
        }
    }

    internal static class VideoEncoders
    {
        // Файл кэша проверок (успехи по «модель GPU + драйвер + кодек + размер»). null — только память процесса.
        public static string CacheFile = null;

        private static readonly object Gate = new object();
        private static Dictionary<string, string> _cache;

        public static List<EncoderInfo> Enumerate()
        {
            List<EncoderInfo> result = new List<EncoderInfo>();
            Exception error = null;
            // MF и D3D — в MTA-потоке: вызывающий может быть STA-потоком интерфейса.
            Thread t = new Thread(() =>
            {
                try { EnumerateCore(result); }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(ApartmentState.MTA);
            t.IsBackground = true;
            t.Start();
            t.Join();
            if (error != null) CapLog.Report(error);
            return result;
        }

        private static void EnumerateCore(List<EncoderInfo> result)
        {
            VidCom.Check(VidNative.MFStartup(0x20070, 0), "MFStartup");
            Dictionary<string, VidDevice> devices = new Dictionary<string, VidDevice>();
            try
            {
                List<VidAdapter> adapters = new List<VidAdapter>();
                HashSet<string> seen = new HashSet<string>();
                foreach (VidAdapter a in VidDxgi.Adapters())
                    if (!a.Software && seen.Add(a.Key)) adapters.Add(a);

                foreach (string codec in new string[] { "h264", "hevc", "av1" })
                {
                    List<EncoderInfo> entries = ListMfts(codec);
                    result.AddRange(entries);
                    foreach (VidAdapter a in adapters)
                    {
                        bool any = false;
                        foreach (EncoderInfo e in entries)
                            if (e.Hardware && (e.Vendor == a.Vendor || e.Vendor == "microsoft")) any = true;
                        if (!any) continue;
                        VidDevice dev;
                        if (!devices.TryGetValue(a.Key, out dev))
                        {
                            try { dev = VidDevice.Create(a); }
                            catch (Exception ex) { CapLog.Write("video: enumerate: " + ex.Message); dev = null; }
                            devices[a.Key] = dev;
                        }
                        if (dev == null) continue;
                        VidEncoderPlan plan = new VidEncoderPlan();
                        plan.Codec = codec; plan.Hardware = true; plan.GpuInput = dev.VideoSupport; plan.Adapter = a;
                        string name, vendor, note;
                        bool hw;
                        bool ok = Test(plan, dev, 640, 360, 30, out name, out vendor, out hw, out note);
                        foreach (EncoderInfo e in entries)
                        {
                            if (!e.Hardware || e.Verified) continue;
                            if (ok && hw && (e.Name == name || (e.Vendor == vendor && e.Vendor == a.Vendor && !NameTaken(entries, name))))
                            {
                                e.Verified = true; e.Adapter = a.Name; e.Note = null;
                                break;
                            }
                            if (!ok && e.Vendor == a.Vendor) { e.Adapter = a.Name; e.Note = note; }
                        }
                    }
                    bool hasSoftware = false;
                    foreach (EncoderInfo e in entries) if (!e.Hardware) hasSoftware = true;
                    if (hasSoftware)
                    {
                        VidEncoderPlan sw = new VidEncoderPlan();
                        sw.Codec = codec;
                        string name, vendor, note;
                        bool hw;
                        bool ok = Test(sw, null, 640, 360, 30, out name, out vendor, out hw, out note);
                        foreach (EncoderInfo e in entries)
                        {
                            if (e.Hardware) continue;
                            if (ok && !hw) { e.Verified = true; e.Note = null; }
                            else e.Note = note;
                            break;
                        }
                    }
                }
            }
            finally
            {
                foreach (VidDevice d in devices.Values) if (d != null) d.Dispose();
                VidNative.MFShutdown();
            }
        }

        private static bool NameTaken(List<EncoderInfo> entries, string name)
        {
            foreach (EncoderInfo e in entries) if (e.Name == name) return true;
            return false;
        }

        private static List<EncoderInfo> ListMfts(string codec)
        {
            List<EncoderInfo> list = new List<EncoderInfo>();
            VidNative.TypeInfo ti = new VidNative.TypeInfo();
            ti.Major = MfA.MediaVideo; ti.Sub = MfA.CodecGuid(codec);
            IntPtr arr;
            uint n;
            if (VidNative.MFTEnumEx(MfA.CatVideoEncoder, 0x3F | 0x40, IntPtr.Zero, ref ti, out arr, out n) < 0) return list;
            for (int i = 0; i < n; i++)
            {
                IntPtr act = Marshal.ReadIntPtr(arr, i * IntPtr.Size);
                EncoderInfo e = new EncoderInfo();
                e.Codec = codec;
                string ven = MfA.Str(act, MfA.MftVendorId);
                e.Hardware = ven != null || MfA.Str(act, MfA.MftHardwareUrl) != null;
                e.Vendor = e.Hardware ? VidDxgi.VendorFromMft(ven) ?? "?" : "software";
                e.Name = MfA.Str(act, MfA.MftFriendlyName) ?? (e.Hardware ? VidEncoderNames.Vendor(e.Vendor) + " " + codec.ToUpperInvariant() + " Encoder MFT" : codec.ToUpperInvariant() + " software encoder");
                list.Add(e);
                IntPtr a = act;
                VidCom.Rel(ref a);
            }
            VidNative.CoTaskMemFree(arr);
            return list;
        }

        private static string Key(VidEncoderPlan plan, int w, int h)
        {
            string gpu = plan.Adapter != null ? plan.Adapter.Key : "sw|" + Environment.OSVersion.Version;
            return gpu + "|" + plan.Codec + "|" + (plan.Hardware ? "hw" : "sw") + "|" + (plan.GpuInput ? "gpu" : "cpu") + "|" + w + "x" + h;
        }

        private static void LoadCache()
        {
            if (_cache != null) return;
            _cache = new Dictionary<string, string>();
            try
            {
                if (CacheFile != null && File.Exists(CacheFile))
                    foreach (string line in File.ReadAllLines(CacheFile))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab > 0) _cache[line.Substring(0, tab)] = line.Substring(tab + 1);
                    }
            }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        private static void SaveCache()
        {
            if (CacheFile == null) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in _cache)
                    if (kv.Value.StartsWith("ok\t", StringComparison.Ordinal)) sb.Append(kv.Key).Append('\t').Append(kv.Value).Append("\r\n");
                string dir = Path.GetDirectoryName(CacheFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(CacheFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        // Пробное кодирование 10 синтетических кадров тем же путём, что и запись. Успехи кэшируются (и в файл),
        // отказы — только в памяти процесса: занятость NVENC другим приложением не должна запоминаться навсегда.
        public static bool Test(VidEncoderPlan plan, VidDevice captureDev, int w, int h, int fps,
                                out string name, out string vendor, out bool hw, out string note)
        {
            string key = Key(plan, w, h);
            lock (Gate)
            {
                LoadCache();
                string cached;
                if (_cache.TryGetValue(key, out cached))
                {
                    string[] p = cached.Split('\t');
                    if (p[0] == "ok" && p.Length >= 4)
                    {
                        name = p[1]; vendor = p[2]; hw = p[3] == "1"; note = "cached";
                        return true;
                    }
                    if (p[0] == "fail")
                    {
                        name = null; vendor = null; hw = false; note = p.Length > 1 ? p[1] : "failed";
                        return false;
                    }
                }
            }
            bool ok = VidEncoderTest.Run(plan, captureDev, w, h, fps, out name, out vendor, out hw, out note);
            lock (Gate)
            {
                _cache[key] = ok ? "ok\t" + name + "\t" + vendor + "\t" + (hw ? "1" : "0") : "fail\t" + (note ?? "").Replace('\t', ' ');
                if (ok) SaveCache();
            }
            return ok;
        }
    }

    internal static class VidEncoderTest
    {
        public static bool Run(VidEncoderPlan plan, VidDevice captureDev, int w, int h, int fps,
                               out string name, out string vendor, out bool hw, out string note)
        {
            name = null; vendor = null; hw = false; note = null;
            string path = Path.Combine(Path.GetTempPath(), "wpc-enctest-" + Guid.NewGuid().ToString("N") + ".mp4");
            VidWriter writer = null;
            List<VidSlot> slots = new List<VidSlot>();
            IntPtr buffer = IntPtr.Zero;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                writer = VidWriter.Create(path, plan, captureDev, w, h, fps, 2000, new List<VidAudioFormat>(), true);
                name = writer.EncoderName; vendor = writer.EncoderVendor; hw = writer.EncoderHardware;
                if (plan.Hardware && !hw) { note = "sink writer picked a software encoder (" + name + ")"; return false; }
                if (plan.Hardware && plan.Adapter != null && vendor != plan.Adapter.Vendor && vendor != "microsoft")
                {
                    note = "encoder vendor " + vendor + " does not match adapter " + plan.Adapter.Vendor;
                    return false;
                }
                long dur = 10000000L / fps;
                if (plan.GpuInput)
                {
                    for (int f = 0; f < 2; f++)
                    {
                        VidSlot s = new VidSlot();
                        s.Texture = CreatePattern(captureDev, w, h, f);
                        VidSampleTracker.Register(s);
                        slots.Add(s);
                    }
                }
                else
                {
                    VidCom.Check(VidNative.MFCreateMemoryBuffer((uint)(w * h * 4), out buffer), "MFCreateMemoryBuffer");
                    IntPtr p; uint max, cur;
                    VidCom.Check(VidCom.Fn<VidCom.DLock>(buffer, 3)(buffer, out p, out max, out cur), "IMFMediaBuffer.Lock");
                    byte[] row = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++) { row[x * 4] = (byte)x; row[x * 4 + 1] = (byte)y; row[x * 4 + 2] = (byte)(x ^ y); row[x * 4 + 3] = 255; }
                        Marshal.Copy(row, 0, new IntPtr(p.ToInt64() + (long)y * w * 4), w * 4);
                    }
                    VidCom.Fn<VidCom.DHr>(buffer, 4)(buffer);
                    VidCom.Fn<VidCom.DUInt>(buffer, 6)(buffer, (uint)(w * h * 4));
                }
                for (int i = 0; i < 10; i++)
                {
                    IntPtr sample;
                    int hr;
                    if (plan.GpuInput) sample = VidSampleTracker.CreateSample(slots[i % 2], out hr);
                    else
                    {
                        hr = VidNative.MFCreateSample(out sample);
                        if (hr >= 0) hr = VidCom.Fn<VidCom.DPtr>(sample, 42)(sample, buffer);
                    }
                    if (sample == IntPtr.Zero || hr < 0) { VidCom.Rel(ref sample); note = "sample " + VidCom.Hex(hr); return false; }
                    VidCom.Fn<VidCom.DLong>(sample, 36)(sample, i * dur);
                    VidCom.Fn<VidCom.DLong>(sample, 38)(sample, dur);
                    hr = writer.Write(writer.VideoStream, sample);
                    VidCom.Rel(ref sample);
                    if (hr < 0) { note = "WriteSample " + VidCom.Hex(hr); return false; }
                }
                int fh = writer.FinalizeWriter();
                if (fh < 0) { note = "Finalize " + VidCom.Hex(fh); return false; }
                writer.Dispose();
                writer = null;
                long len = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (len <= 0) { note = "empty output"; return false; }
                note = "ok " + sw.ElapsedMilliseconds + " ms";
                return true;
            }
            catch (Exception ex)
            {
                note = ex.Message;
                return false;
            }
            finally
            {
                if (writer != null) writer.Dispose();
                VidCom.Rel(ref buffer);
                foreach (VidSlot s in slots)
                {
                    VidSampleTracker.Unregister(s);
                    VidCom.Rel(ref s.Texture);
                }
                try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { CapLog.Report(ex); }
                CapLog.Write("video: encoder test " + plan.Describe() + " " + w + "x" + h + " -> " + (name ?? "-") + " : " + note);
            }
        }

        private static IntPtr CreatePattern(VidDevice dev, int w, int h, int seed)
        {
            byte[] px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    px[o] = (byte)(x + seed * 40); px[o + 1] = (byte)y; px[o + 2] = (byte)((x + y) >> 1); px[o + 3] = 255;
                }
            VidCom.TexDesc td = new VidCom.TexDesc();
            td.Width = (uint)w; td.Height = (uint)h; td.MipLevels = 1; td.ArraySize = 1; td.Format = 87; td.SampleCount = 1; td.BindFlags = 0x28;
            GCHandle gh = GCHandle.Alloc(px, GCHandleType.Pinned);
            try
            {
                VidCom.SubresData sd = new VidCom.SubresData();
                sd.Data = gh.AddrOfPinnedObject(); sd.Pitch = (uint)(w * 4);
                IntPtr tex;
                VidCom.Check(VidCom.Fn<VidCom.DCreateTexInit>(dev.Device, 5)(dev.Device, ref td, ref sd, out tex), "CreateTexture2D(pattern)");
                return tex;
            }
            finally { gh.Free(); }
        }
    }

    // ------------------------------------------------------------------ //
    //  Запись
    // ------------------------------------------------------------------ //
    internal sealed class VideoRecorder : IDisposable
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

        private string Loop()
        {
            int fps = _o.Fps;
            long nextIndex = 0;
            long maxFrames = _o.MaxDuration > TimeSpan.Zero ? _o.MaxDuration.Ticks * fps / 10000000L : long.MaxValue;
            long lastCheck = Stopwatch.GetTimestamp(), lastAudio = 0;
            long statQpc = lastCheck, statFrames = 0, statUnique = 0;
            while (!_stopRequested)
            {
                _source.Poll(_onFrame);
                if (_source.Lost) { CapLog.Write("video: source lost: " + _source.LostReason); return "device-lost"; }
                if (_writerHr < 0) return IsDeviceLost(_writerHr) ? "device-lost" : "error: " + _writerError;

                long now = Stopwatch.GetTimestamp();
                bool paused;
                long pausedTotal;
                lock (_pauseGate) { paused = _paused; pausedTotal = _pausedTotal100; }

                if (now - lastAudio >= Freq / 100)
                {
                    PumpAudio();
                    lastAudio = now;
                }

                if (!paused)
                {
                    long elapsed = To100(now - _startQpc) - pausedTotal;
                    long target = elapsed * fps / 10000000L;
                    if (target >= nextIndex)
                    {
                        TakePending(now);
                        if (target - nextIndex > fps)
                        {
                            // Отстали больше чем на секунду (система висела): пропуск вместо лавины повторов.
                            Interlocked.Add(ref _dropped, target - nextIndex);
                            nextIndex = target;
                        }
                        for (; nextIndex <= target; nextIndex++)
                        {
                            if (nextIndex >= maxFrames) return "limit-duration";
                            EmitVideo(nextIndex);
                        }
                    }
                }

                if (now - lastCheck >= Freq / 2)
                {
                    double dt = (now - statQpc) / (double)Freq;
                    if (dt >= 1.0)
                    {
                        long f = Interlocked.Read(ref _framesWritten), u = Interlocked.Read(ref _uniqueFrames);
                        _actualFps = (f - statFrames) / dt;
                        _capturedFps = (u - statUnique) / dt;
                        statFrames = f; statUnique = u; statQpc = now;
                    }
                    lastCheck = now;
                    string reason = CheckLimits();
                    if (reason != null) return reason;
                }

                // Сон до следующего кадра; источник опрашивается примерно раз в миллисекунду.
                long wait100 = paused ? 50000 : (nextIndex * 10000000L / fps) - (To100(Stopwatch.GetTimestamp() - _startQpc) - pausedTotal);
                if (wait100 > 15000) Thread.Sleep(1);
                else if (wait100 > 0) Thread.Sleep(0);
            }
            // Хвост звука до остановки.
            PumpAudio();
            return null;
        }

        private string CheckLimits()
        {
            try
            {
                FileInfo fi = new FileInfo(_o.Path);
                if (fi.Exists) Interlocked.Exchange(ref _bytes, fi.Length);
            }
            catch (IOException) { }
            if (_o.MaxBytes > 0 && Interlocked.Read(ref _bytes) >= _o.MaxBytes) return "limit-size";
            if (_o.MinFreeBytes > 0)
            {
                ulong free, total, totalFree;
                string dir = Path.GetDirectoryName(Path.GetFullPath(_o.Path));
                if (VidNative.GetDiskFreeSpaceEx(dir, out free, out total, out totalFree) && (long)Math.Min(free, (ulong)long.MaxValue) < _o.MinFreeBytes) return "low-disk";
            }
            int removed = _dev.RemovedReason();
            if (removed != 0) { CapLog.Write("video: device removed " + VidCom.Hex(removed)); return "device-lost"; }
            if (_o.MaxDuration > TimeSpan.Zero && Elapsed >= _o.MaxDuration) return "limit-duration";
            return null;
        }

        // Новый кадр источника → текстура «ожидающая» (ещё не отдана кодировщику, её можно перезаписывать).
        private void OnFrame(IntPtr tex, int w, int h, uint fmt)
        {
            VidSlot target = _pending ?? AcquireSlot();
            if (target == null) { Interlocked.Increment(ref _noSlot); return; }
            Render(target, tex, w, h, fmt);
            _pending = target;
            _pendingSynthetic = false;
            Interlocked.Increment(ref _uniqueFrames);
        }

        private void Render(VidSlot slot, IntPtr tex, int w, int h, uint fmt)
        {
            if (_windowMode)
            {
                double s = Math.Min(1.0, Math.Min((double)_outW / w, (double)_outH / h));
                int dw = Math.Max(1, Math.Min(_outW, (int)Math.Round(w * s))), dh = Math.Max(1, Math.Min(_outH, (int)Math.Round(h * s)));
                Rectangle dst = new Rectangle((_outW - dw) / 2, (_outH - dh) / 2, dw, dh);
                bool margins = dw != _outW || dh != _outH;
                if (dw == w && dh == h && fmt == 87)
                {
                    if (margins) _dev.Clear(slot.Rtv);
                    _dev.CopyRegion(slot.Texture, dst.X, dst.Y, tex, 0, 0, w, h);
                }
                else Blit(slot, tex, fmt, new Rectangle(0, 0, w, h), dst, margins);
                return;
            }
            Rectangle c = _crop;
            Rectangle avail = Rectangle.Intersect(c, new Rectangle(0, 0, w, h));
            if (c.Width == _outW && c.Height == _outH && fmt == 87)
            {
                if (avail != c) _dev.Clear(slot.Rtv);
                _dev.CopyRegion(slot.Texture, avail.X - c.X, avail.Y - c.Y, tex, avail.X, avail.Y, avail.Width, avail.Height);
            }
            else Blit(slot, tex, fmt, avail, new Rectangle(0, 0, _outW, _outH), avail != c);
            if (_cursorDraw)
                VidCursor.Draw(slot.Texture, _output.Bounds.X + c.X, _output.Bounds.Y + c.Y, (double)_outW / c.Width, 0, 0);
        }

        private void Blit(VidSlot slot, IntPtr tex, uint fmt, Rectangle src, Rectangle dst, bool clear)
        {
            if (src.Width <= 0 || src.Height <= 0) { _dev.Clear(slot.Rtv); return; }
            if (_blit == null)
            {
                if (fmt != 87) throw new VideoException("Captured pixel format " + fmt + " needs the GPU converter, which is unavailable", unchecked((int)0x887A0004));
                _dev.Clear(slot.Rtv);
                _dev.CopyRegion(slot.Texture, dst.X, dst.Y, tex, src.X, src.Y, Math.Min(src.Width, dst.Width), Math.Min(src.Height, dst.Height));
                return;
            }
            VidCom.TexDesc d = VidDevice.Desc(tex);
            IntPtr srcTex = tex;
            int tw = (int)d.Width, th = (int)d.Height;
            RectangleF r = src;
            if ((d.BindFlags & 8) == 0 || d.SampleCount > 1)
            {
                // Текстура источника без SHADER_RESOURCE: сперва копия нужного прямоугольника в свою текстуру.
                if (_srcCopy == IntPtr.Zero || _srcCopyW != src.Width || _srcCopyH != src.Height || _srcCopyFmt != d.Format)
                {
                    VidCom.Rel(ref _srcCopy);
                    _srcCopy = _dev.CreateTexture(src.Width, src.Height, d.Format, 8, 0, 0, 0);
                    _srcCopyW = src.Width; _srcCopyH = src.Height; _srcCopyFmt = d.Format;
                }
                _dev.CopyRegion(_srcCopy, 0, 0, tex, src.X, src.Y, src.Width, src.Height);
                srcTex = _srcCopy; tw = src.Width; th = src.Height;
                r = new RectangleF(0, 0, src.Width, src.Height);
            }
            _blit.Draw(srcTex, tw, th, r, slot.Rtv, dst, clear, fmt == 10);
        }

        // На тике кадра ожидающая текстура становится «последней»; в режиме CPU её содержимое уходит в память.
        private void TakePending(long now)
        {
            if (_pending == null) return;
            VidSlot p = _pending;
            bool synthetic = _pendingSynthetic;
            _pending = null;
            _pendingSynthetic = false;
            if (!_plan.GpuInput)
            {
                IntPtr buf = Readback(p.Texture);
                VidCom.Rel(ref _lastBuffer);
                _lastBuffer = buf;
            }
            _last = p;
            if (!_blackDone && !synthetic)
            {
                if (now - _startQpc > Freq)
                {
                    _blackDone = true;
                }
                else if (_blackChecks == 0 || now - _lastBlackCheckQpc >= Freq / 5)
                {
                    _lastBlackCheckQpc = now;
                    _blackChecks++;
                    if (!IsDark(p.Texture)) _blackAllDark = false;
                    _black = _blackAllDark;
                }
            }
            else if (!_blackDone && now - _startQpc > Freq) _blackDone = true;
        }

        private IntPtr Readback(IntPtr tex)
        {
            _dev.Copy(_staging, tex);
            VidCom.Mapped m;
            VidCom.Check(_dev.Map(_staging, out m), "Map(staging)");
            IntPtr buf = IntPtr.Zero;
            try
            {
                int size = _outW * _outH * 4;
                VidCom.Check(VidNative.MFCreateMemoryBuffer((uint)size, out buf), "MFCreateMemoryBuffer");
                IntPtr p; uint max, cur;
                VidCom.Check(VidCom.Fn<VidCom.DLock>(buf, 3)(buf, out p, out max, out cur), "IMFMediaBuffer.Lock");
                int row = _outW * 4;
                if (m.RowPitch == row) VidNative.CopyMemory(p, m.Data, new UIntPtr((uint)size));
                else
                    for (int y = 0; y < _outH; y++)
                        VidNative.CopyMemory(new IntPtr(p.ToInt64() + (long)y * row), new IntPtr(m.Data.ToInt64() + (long)y * m.RowPitch), new UIntPtr((uint)row));
                VidCom.Fn<VidCom.DHr>(buf, 4)(buf);
                VidCom.Fn<VidCom.DUInt>(buf, 6)(buf, (uint)size);
                IntPtr r = buf;
                buf = IntPtr.Zero;
                return r;
            }
            finally
            {
                _dev.Unmap(_staging);
                VidCom.Rel(ref buf);
            }
        }

        // Пять участков 32x32 (центр и четверти): все почти чёрные → вероятно, эксклюзивный полноэкранный режим или DRM.
        private bool IsDark(IntPtr tex)
        {
            int p = Math.Min(32, Math.Min(_outW, _outH));
            double[,] at = { { 0.5, 0.5 }, { 0.25, 0.25 }, { 0.75, 0.25 }, { 0.25, 0.75 }, { 0.75, 0.75 } };
            for (int k = 0; k < 5; k++)
            {
                int x = Math.Max(0, Math.Min(_outW - p, (int)(_outW * at[k, 0]) - p / 2));
                int y = Math.Max(0, Math.Min(_outH - p, (int)(_outH * at[k, 1]) - p / 2));
                _dev.CopyRegion(_blackStaging, k * 32, 0, tex, x, y, p, p);
            }
            VidCom.Mapped m;
            if (_dev.Map(_blackStaging, out m) < 0) return false;
            try
            {
                byte[] row = new byte[32 * 5 * 4];
                int max = 0;
                for (int y = 0; y < p; y++)
                {
                    Marshal.Copy(new IntPtr(m.Data.ToInt64() + (long)y * m.RowPitch), row, 0, row.Length);
                    for (int k = 0; k < 5; k++)
                        for (int x = 0; x < p; x++)
                        {
                            int o = (k * 32 + x) * 4;
                            int v = Math.Max(row[o], Math.Max(row[o + 1], row[o + 2]));
                            if (v > max) max = v;
                        }
                }
                return max < 24;
            }
            finally { _dev.Unmap(_blackStaging); }
        }

        private void EmitVideo(long index)
        {
            if (_last == null) return;
            lock (_qGate)
            {
                if (_queuedVideo > _o.Fps)
                {
                    Interlocked.Increment(ref _dropped);
                    return;
                }
            }
            IntPtr sample;
            int hr;
            if (_plan.GpuInput) sample = VidSampleTracker.CreateSample(_last, out hr);
            else
            {
                hr = VidNative.MFCreateSample(out sample);
                if (hr >= 0) hr = VidCom.Fn<VidCom.DPtr>(sample, 42)(sample, _lastBuffer);
            }
            if (sample == IntPtr.Zero || hr < 0)
            {
                VidCom.Rel(ref sample);
                throw new VideoException("Video sample creation failed, HRESULT " + VidCom.Hex(hr), hr);
            }
            long ts = index * 10000000L / _o.Fps;
            long next = (index + 1) * 10000000L / _o.Fps;
            VidCom.Fn<VidCom.DLong>(sample, 36)(sample, ts);
            VidCom.Fn<VidCom.DLong>(sample, 38)(sample, next - ts);
            Enqueue(_writer.VideoStream, sample, true);
            Interlocked.Increment(ref _framesWritten);
        }

        private void PumpAudio()
        {
            for (int t = 0; t < _audio.Count; t++)
            {
                if (_audioBroken[t]) continue;
                IAudioSource src = _audio[t];
                VidAudioFormat af = _audioFmt[t];
                float[] fb = _audioBuf[t];
                short[] pcm = _pcmBuf[t];
                for (int guard = 0; guard < 64; guard++)
                {
                    int n;
                    long ts;
                    try { n = src.Read(fb, fb.Length / af.Channels, out ts); }
                    catch (Exception ex)
                    {
                        _audioBroken[t] = true;
                        CapLog.Write("video: audio track " + t + " failed, continuing without it: " + ex.Message);
                        AddNote("audio track " + t + " failed");
                        break;
                    }
                    if (n <= 0) break;
                    long mapped;
                    if (!MapAudioTime(ts, out mapped)) continue;
                    int count = n * af.Channels;
                    for (int i = 0; i < count; i++)
                    {
                        float v = fb[i];
                        pcm[i] = v >= 1f ? short.MaxValue : v <= -1f ? short.MinValue : (short)(v * 32767f);
                    }
                    IntPtr buf = IntPtr.Zero, sample = IntPtr.Zero;
                    try
                    {
                        VidCom.Check(VidNative.MFCreateMemoryBuffer((uint)(count * 2), out buf), "MFCreateMemoryBuffer(audio)");
                        IntPtr p; uint max, cur;
                        VidCom.Check(VidCom.Fn<VidCom.DLock>(buf, 3)(buf, out p, out max, out cur), "IMFMediaBuffer.Lock(audio)");
                        Marshal.Copy(pcm, 0, p, count);
                        VidCom.Fn<VidCom.DHr>(buf, 4)(buf);
                        VidCom.Fn<VidCom.DUInt>(buf, 6)(buf, (uint)(count * 2));
                        VidCom.Check(VidNative.MFCreateSample(out sample), "MFCreateSample(audio)");
                        VidCom.Fn<VidCom.DPtr>(sample, 42)(sample, buf);
                        VidCom.Fn<VidCom.DLong>(sample, 36)(sample, mapped);
                        VidCom.Fn<VidCom.DLong>(sample, 38)(sample, n * 10000000L / af.Rate);
                        Enqueue(_writer.AudioStreams[t], sample, false);
                        sample = IntPtr.Zero;
                    }
                    finally
                    {
                        VidCom.Rel(ref sample);
                        VidCom.Rel(ref buf);
                    }
                }
            }
        }

        // Время звука без пауз; кусок, начавшийся во время паузы, выбрасывается.
        private bool MapAudioTime(long ts, out long mapped)
        {
            mapped = 0;
            lock (_pauseGate)
            {
                long sub = 0;
                foreach (long[] iv in _pauses)
                {
                    if (ts >= iv[0] && ts < iv[1]) return false;
                    if (ts >= iv[1]) sub += iv[1] - iv[0];
                }
                if (_paused && ts >= To100(_pauseStartQpc - _startQpc)) return false;
                mapped = ts - sub;
            }
            return mapped >= 0;
        }

        private void Enqueue(uint stream, IntPtr sample, bool video)
        {
            QItem it = new QItem();
            it.Stream = stream; it.Sample = sample; it.Video = video;
            lock (_qGate)
            {
                _queue.Enqueue(it);
                if (video) _queuedVideo++;
                Monitor.Pulse(_qGate);
            }
        }

        // ---- поток писателя: WriteSample по очереди и Finalize ----
        private void WriterMain()
        {
            try
            {
                while (true)
                {
                    QItem it;
                    lock (_qGate)
                    {
                        while (_queue.Count == 0 && !_writerStop) Monitor.Wait(_qGate);
                        if (_queue.Count == 0) break;
                        it = _queue.Dequeue();
                        if (it.Video) _queuedVideo--;
                    }
                    if (_writerHr >= 0)
                    {
                        int hr = _writer.Write(it.Stream, it.Sample);
                        if (hr < 0)
                        {
                            _writerError = "WriteSample(" + (it.Video ? "video" : "audio") + ") HRESULT " + VidCom.Hex(hr);
                            _writerHr = hr;
                            CapLog.Write("video: " + _writerError);
                        }
                    }
                    IntPtr s = it.Sample;
                    VidCom.Rel(ref s);
                }
                int fh = _writer.FinalizeWriter();
                if (fh < 0) CapLog.Write("video: Finalize HRESULT " + VidCom.Hex(fh) + " " + _o.Path);
            }
            catch (Exception ex) { CapLog.Report(ex); }
            finally { _writerDone.Set(); }
        }

        // Возвращает true, если писатель брошен (Finalize завис): тогда MF нельзя выключать.
        private bool Shutdown()
        {
            lock (_pauseGate)
            {
                if (_running) _finalElapsed100 = Math.Max(0, ElapsedLocked(Stopwatch.GetTimestamp()));
            }
            foreach (IAudioSource a in _audio)
                try { a.Stop(); } catch (Exception ex) { CapLog.Report(ex); }

            bool abandoned = false;
            if (_writerThread != null)
            {
                lock (_qGate) { _writerStop = true; Monitor.Pulse(_qGate); }
                if (!_writerDone.WaitOne(9000))
                {
                    abandoned = true;
                    _writer.Abandoned = true;
                    CapLog.Write("video: writer did not finish in 9 s, abandoned: " + _o.Path);
                }
            }
            if (_writer != null) _writer.Dispose();

            foreach (IAudioSource a in _audio)
                try { a.Dispose(); } catch (Exception ex) { CapLog.Report(ex); }
            if (_source != null) try { _source.Dispose(); } catch (Exception ex) { CapLog.Report(ex); }
            foreach (VidSlot s in _slots)
            {
                VidSampleTracker.Unregister(s);
                VidCom.Rel(ref s.Rtv);
                VidCom.Rel(ref s.Texture);
            }
            _slots.Clear();
            VidCom.Rel(ref _lastBuffer);
            VidCom.Rel(ref _staging);
            VidCom.Rel(ref _srcCopy);
            VidCom.Rel(ref _blackStaging);
            if (_blit != null) _blit.Dispose();
            if (_dev != null && !abandoned) _dev.Dispose();
            try
            {
                FileInfo fi = new FileInfo(_o.Path);
                if (fi.Exists) Interlocked.Exchange(ref _bytes, fi.Length);
            }
            catch (IOException) { }
            return abandoned;
        }
    }

    // ------------------------------------------------------------------ //
    //  Починка незавершённого fMP4: перемукс без перекодирования
    // ------------------------------------------------------------------ //
    internal static class FragmentedMp4
    {
        // Читает все целые фрагменты SourceReader'ом и пишет их Sink Writer'ом в обычный MP4 с длительностью.
        // Исходник не трогает; результат — «имя.repaired.mp4» рядом, только если он открывается и длительность > 0.
        public static bool Repair(string path, out string repairedPath)
        {
            repairedPath = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            bool ok = false;
            string outPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFileNameWithoutExtension(path) + ".repaired.mp4");
            Exception error = null;
            Thread t = new Thread(() =>
            {
                try { ok = RepairCore(path, outPath); }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(ApartmentState.MTA);
            t.IsBackground = true;
            t.Start();
            t.Join();
            if (error != null) CapLog.Report(error);
            if (ok) repairedPath = outPath;
            else try { if (File.Exists(outPath)) File.Delete(outPath); } catch (IOException) { }
            return ok;
        }

        private static bool RepairCore(string path, string outPath)
        {
            VidCom.Check(VidNative.MFStartup(0x20070, 0), "MFStartup");
            IntPtr reader = IntPtr.Zero, writer = IntPtr.Zero, attrs = IntPtr.Zero;
            try
            {
                if (VidNative.MFCreateSourceReaderFromURL(path, IntPtr.Zero, out reader) < 0) return false;
                List<uint> map = new List<uint>();
                List<IntPtr> types = new List<IntPtr>();
                try
                {
                    for (uint i = 0; i < 16; i++)
                    {
                        IntPtr type;
                        if (VidCom.Fn<VidCom.DUIntUIntOutPtr>(reader, 5)(reader, i, 0, out type) < 0) break;
                        types.Add(type);
                        VidCom.Fn<VidCom.DUIntInt>(reader, 4)(reader, i, 1);
                    }
                    if (types.Count == 0) return false;
                    if (File.Exists(outPath)) File.Delete(outPath);
                    VidCom.Check(VidNative.MFCreateAttributes(out attrs, 2), "MFCreateAttributes");
                    MfA.G(attrs, MfA.ContainerType, MfA.ContainerMp4);
                    VidCom.Check(VidNative.MFCreateSinkWriterFromURL(outPath, IntPtr.Zero, attrs, out writer), "MFCreateSinkWriterFromURL(repair)");
                    foreach (IntPtr type in types)
                    {
                        uint idx;
                        VidCom.Check(VidCom.Fn<VidCom.DPtrOutUInt>(writer, 3)(writer, type, out idx), "AddStream(repair)");
                        VidCom.Check(VidCom.Fn<VidCom.DUIntPtrPtr>(writer, 4)(writer, idx, type, IntPtr.Zero), "SetInputMediaType(passthrough)");
                        map.Add(idx);
                    }
                }
                finally
                {
                    for (int i = 0; i < types.Count; i++) { IntPtr p = types[i]; VidCom.Rel(ref p); }
                }
                VidCom.Check(VidCom.Fn<VidCom.DHr>(writer, 5)(writer), "BeginWriting(repair)");
                VidCom.DReadSample read = VidCom.Fn<VidCom.DReadSample>(reader, 9);
                int ended = 0;
                long written = 0;
                bool[] done = new bool[map.Count];
                while (ended < map.Count)
                {
                    uint actual, flags;
                    long ts;
                    IntPtr sample;
                    int hr = read(reader, 0xFFFFFFFE, 0, out actual, out flags, out ts, out sample);
                    if (hr < 0 || (flags & 1) != 0) { VidCom.Rel(ref sample); break; } // оборванный хвост: берём, что прочиталось
                    if (sample != IntPtr.Zero)
                    {
                        if (actual < map.Count)
                        {
                            int wh = VidCom.Fn<VidCom.DUIntPtr>(writer, 6)(writer, map[(int)actual], sample);
                            if (wh >= 0) written++;
                        }
                        VidCom.Rel(ref sample);
                    }
                    if ((flags & 2) != 0 && actual < map.Count && !done[actual]) { done[actual] = true; ended++; }
                }
                if (written == 0) return false;
                if (VidCom.Fn<VidCom.DHr>(writer, 11)(writer) < 0) return false;
                VidCom.Rel(ref writer);
                long dur = Duration(outPath);
                CapLog.Write("video: repaired " + path + " -> " + outPath + ", samples " + written + ", duration " + (dur / 10000) + " ms");
                return dur > 0;
            }
            finally
            {
                VidCom.Rel(ref writer);
                VidCom.Rel(ref attrs);
                VidCom.Rel(ref reader);
                VidNative.MFShutdown();
            }
        }

        // MF_PD_DURATION файла в 100 нс; -1, если прочитать не удалось.
        public static long Duration(string path)
        {
            IntPtr reader;
            if (VidNative.MFCreateSourceReaderFromURL(path, IntPtr.Zero, out reader) < 0) return -1;
            IntPtr pv = Marshal.AllocHGlobal(24);
            try
            {
                for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
                Guid key = MfA.PdDuration;
                if (VidCom.Fn<VidCom.DPresAttr>(reader, 12)(reader, 0xFFFFFFFF, ref key, pv) < 0) return -1;
                long v = Marshal.ReadInt16(pv) == 21 ? Marshal.ReadInt64(pv, 8) : -1;
                VidNative.PropVariantClear(pv);
                return v;
            }
            finally
            {
                Marshal.FreeHGlobal(pv);
                VidCom.Rel(ref reader);
            }
        }
    }
}
