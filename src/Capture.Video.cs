// SysDeck — «Захват»: движок записи видео (экран, область, окно) в fMP4.
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

namespace SysDeck.Capture
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
}
