// SysDeck — «Захват»: план кодировщика, IMFSinkWriter, выбор и проверка аппаратных кодировщиков, fMP4.
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
