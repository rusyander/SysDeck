// SysDeck — «Захват»: источники кадров (WGC, DDA), курсор, пул текстур и учёт кадров.
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
}
