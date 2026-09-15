// SysDeck — кадры для оверлея: FPS, время кадра, 1 % и 0,1 % худших кадров, фризы и «пила» времени
// кадра. Источник — собственная сессия ETW реального времени, без внедрения в игру и без драйвера:
//  * Microsoft-Windows-DXGI, событие 42 Present_Start — Direct3D 10/11/12 и всё, что выводит кадры через DXGI;
//  * Microsoft-Windows-D3D9, событие 1 Present_Start — Direct3D 9;
//  * Microsoft-Windows-DxgKrnl, события 184 Present и 215 PresentHistoryDetailed — запасной путь для OpenGL и Vulkan,
//    которые мимо DXGI. Они учитываются, только если у процесса нет событий DXGI/D3D9 (иначе кадр посчитался бы дважды).
// Идентификаторы и ключевые слова сверены с манифестами провайдеров (wevtutil gp … /ge /gm).
//
// Права: сессию ETW может завести администратор или член группы «Пользователи журналов производительности»
// (S-1-5-32-559). Оверлей с правами (задача Планировщика) может сразу; без прав человек один раз добавляет себя в
// группу через помощник с правами (--elevated-job «perflog»), после чего нужно выйти из Windows и войти снова —
// группа попадает в токен только при входе.
//
// Кадры считаются для процесса активного окна. Время — метки QPC из самих событий, поэтому задержка доставки
// буферов ETW (до секунды) не искажает расчёт, а только сдвигает его.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace SysDeck.Capture
{
    // ------------------------------------------------------------------ //
    //  Расчёт по меткам кадров — без ETW, проверяется тестами
    // ------------------------------------------------------------------ //
    internal struct HudFpsResult
    {
        public double Fps, FrameMs, Low1, Low01, StuttersPerMin;
        public double[] Series;        // время последних кадров, мс, от старого к новому — для «пилы»
        public int Frames;             // кадров в окне расчёта «худших»
    }

    internal static class HudFrameMath
    {
        public const int SeriesMax = 240;
        public const double LowWindowSec = 30, StutterWindowSec = 60, AliveSec = 30;

        // Фриз: кадр не короче 25 мс и в 2,5 раза длиннее медианы предыдущих (до 15) кадров.
        public const double StutterFactor = 2.5, StutterMinMs = 25;

        // ts — метки кадров по возрастанию, now — «сейчас» в тех же единицах, freq — единиц в секунде.
        public static HudFpsResult Compute(long[] ts, int count, long now, long freq)
        {
            HudFpsResult r = new HudFpsResult();
            r.Fps = r.FrameMs = r.Low1 = r.Low01 = r.StuttersPerMin = double.NaN;
            if (ts == null || count <= 0 || freq <= 0) return r;
            long last = ts[count - 1];
            if (now < last) now = last;
            // Давно нет кадров — процесс не рисует, и нечего показывать.
            if (now - last > (long)(AliveSec * freq)) return r;

            // FPS за последнюю секунду. Окно не заполнено (кадры пошли недавно) — по охвату имеющихся.
            long from1 = now - freq;
            int n1 = 0, first1 = -1;
            for (int i = count - 1; i >= 0 && ts[i] > from1; i--) { n1++; first1 = i; }
            if (count > n1)            r.Fps = n1;                                   // до окна кадры были — окно полное
            else if (n1 >= 2)          r.Fps = (n1 - 1) * (double)freq / (last - ts[first1]);
            else                       r.Fps = 0;

            // Время кадра — среднее за последние 250 мс, хотя бы по одному последнему кадру.
            long from250 = last - freq / 4;
            double sum = 0; int nMs = 0;
            for (int i = count - 1; i >= 1; i--)
            {
                if (nMs > 0 && ts[i] < from250) break;
                sum += (ts[i] - ts[i - 1]) * 1000.0 / freq;
                nMs++;
            }
            if (nMs > 0) r.FrameMs = sum / nMs;

            // Худшие кадры за 30 с: среднее самых долгих 1 % и 0,1 % кадров, переведённое в FPS.
            long fromLow = now - (long)(LowWindowSec * freq);
            List<double> deltas = new List<double>();
            for (int i = 1; i < count; i++)
                if (ts[i] >= fromLow) deltas.Add((ts[i] - ts[i - 1]) * 1000.0 / freq);
            r.Frames = deltas.Count;
            if (deltas.Count >= 10)
            {
                deltas.Sort();
                r.Low1 = 1000.0 / WorstAverage(deltas, deltas.Count / 100);
                r.Low01 = 1000.0 / WorstAverage(deltas, deltas.Count / 1000);
            }

            // Фризы за минуту; кольцо охватывает меньше минуты — пересчёт на минуту по охвату.
            long fromSt = now - (long)(StutterWindowSec * freq);
            int stutters = 0, start = -1;
            double[] prev = new double[15];
            for (int i = 1; i < count; i++)
            {
                if (ts[i] < fromSt) continue;
                if (start < 0) start = i - 1;
                double d = (ts[i] - ts[i - 1]) * 1000.0 / freq;
                int k = 0;
                for (int j = i - 1; j >= 1 && k < prev.Length; j--, k++) prev[k] = (ts[j] - ts[j - 1]) * 1000.0 / freq;
                if (k >= 5 && d >= StutterMinMs && d >= StutterFactor * Median(prev, k)) stutters++;
            }
            if (start >= 0)
            {
                double span = Math.Max((double)(now - ts[start]) / freq, 1.0);
                r.StuttersPerMin = span >= StutterWindowSec ? stutters : stutters * 60.0 / span;
            }

            int ns = Math.Min(SeriesMax, count - 1);
            r.Series = new double[Math.Max(0, ns)];
            for (int i = 0; i < ns; i++)
            {
                int at = count - ns + i;
                r.Series[i] = (ts[at] - ts[at - 1]) * 1000.0 / freq;
            }
            return r;
        }

        // sorted — по возрастанию; k худших = k самых длинных, не меньше одного.
        private static double WorstAverage(List<double> sorted, int k)
        {
            k = Math.Max(1, Math.Min(sorted.Count, k));
            double s = 0;
            for (int i = sorted.Count - k; i < sorted.Count; i++) s += sorted[i];
            return s / k;
        }

        private static double Median(double[] a, int n)
        {
            double[] c = new double[n];
            Array.Copy(a, c, n);
            Array.Sort(c);
            return (n & 1) == 1 ? c[n / 2] : (c[n / 2 - 1] + c[n / 2]) / 2;
        }
    }

    // ------------------------------------------------------------------ //
    //  Кадры по процессам: поток ETW складывает, сборщик читает
    // ------------------------------------------------------------------ //
    internal sealed class HudPresentTracker
    {
        public const int KernelLayer = 1, UserLayer = 0;
        private const int Capacity = 16384, MaxSeries = 256;

        private sealed class Series
        {
            public int Pid, Layer;
            public ulong Key;
            public readonly long[] Ring = new long[Capacity];
            public int Head, Count;
            public long Last;

            public void Push(long t)
            {
                // События приходят из буферов разных процессоров не строго по порядку — чуть более раннюю метку
                // вставлять не стоит, достаточно не дать ей сломать возрастание.
                if (Count > 0 && t < Last) t = Last;
                Ring[Head] = t;
                Head = (Head + 1) % Ring.Length;
                if (Count < Ring.Length) Count++;
                Last = t;
            }

            public long[] Copy()
            {
                long[] a = new long[Count];
                for (int i = 0; i < Count; i++) a[i] = Ring[((Head - Count + i) % Ring.Length + Ring.Length) % Ring.Length];
                return a;
            }

            public int CountSince(long from)
            {
                int n = 0;
                for (int i = 0; i < Count; i++)
                {
                    long t = Ring[((Head - 1 - i) % Ring.Length + Ring.Length) % Ring.Length];
                    if (t <= from) break;
                    n++;
                }
                return n;
            }
        }

        private readonly object _gate = new object();
        private readonly List<Series> _series = new List<Series>();
        private long _latest;

        public long Latest { get { lock (_gate) return _latest; } }

        public void Seen(long qpc)
        {
            lock (_gate) if (qpc > _latest) _latest = qpc;
        }

        public void Present(int pid, int layer, ulong key, long qpc)
        {
            lock (_gate)
            {
                if (qpc > _latest) _latest = qpc;
                Series s = null;
                foreach (Series x in _series)
                    if (x.Pid == pid && x.Layer == layer && x.Key == key) { s = x; break; }
                if (s == null)
                {
                    if (_series.Count >= MaxSeries)
                    {
                        Series oldest = _series[0];
                        foreach (Series x in _series) if (x.Last < oldest.Last) oldest = x;
                        _series.Remove(oldest);
                    }
                    s = new Series();
                    s.Pid = pid; s.Layer = layer; s.Key = key;
                    _series.Add(s);
                }
                s.Push(qpc);
            }
        }

        public int SeriesCount { get { lock (_gate) return _series.Count; } }

        // Как процесс выводит кадры: API, синхронизация, модель вывода из событий ядра. Хранится последнее.
        private readonly Dictionary<int, HudPresentInfo> _info = new Dictionary<int, HudPresentInfo>();
        private long _modelSeenAt;

        private HudPresentInfo InfoFor(int pid)
        {
            HudPresentInfo i;
            if (_info.TryGetValue(pid, out i)) return i;
            if (_info.Count >= MaxSeries) _info.Clear();
            i = new HudPresentInfo();
            _info[pid] = i;
            return i;
        }

        public void NoteApi(int pid, string api, int sync, bool tearing, long qpc)
        {
            lock (_gate)
            {
                HudPresentInfo i = InfoFor(pid);
                i.Api = api; i.Sync = sync; i.Tearing = tearing; i.ApiAt = qpc;
            }
        }

        public void NoteModel(int pid, int model, long qpc)
        {
            lock (_gate)
            {
                HudPresentInfo i = InfoFor(pid);
                i.Model = model; i.ModelAt = qpc;
                _modelSeenAt = qpc;
            }
        }

        // Копия; null — о процессе ничего не известно. ModelSeenAnyAt — когда ядро вообще сообщало модель вывода.
        public HudPresentInfo Info(int pid)
        {
            lock (_gate)
            {
                HudPresentInfo i;
                if (!_info.TryGetValue(pid, out i)) return null;
                HudPresentInfo c = i.Clone();
                c.ModelSeenAnyAt = _modelSeenAt;
                return c;
            }
        }

        public List<int> Pids()
        {
            List<int> pids = new List<int>();
            lock (_gate) foreach (Series x in _series) if (!pids.Contains(x.Pid)) pids.Add(x.Pid);
            return pids;
        }

        // Метки лучшей цепочки кадров процесса: живая (кадр за последние 2 с) цепочка DXGI/D3D9 — у окна их бывает
        // несколько, берётся самая частая за секунду; нет живых — ядро; нет и их — самая свежая из затихших (FPS 0).
        // null — процесс кадров не выводил.
        public long[] Pick(int pid, long now, long freq, long dropOlderThan)
        {
            lock (_gate)
            {
                _series.RemoveAll(delegate(Series x) { return x.Last < dropOlderThan; });
                Series best = null;
                int bestCount = -1;
                for (int layer = UserLayer; layer <= KernelLayer && best == null; layer++)
                    foreach (Series x in _series)
                    {
                        if (x.Pid != pid || x.Layer != layer || x.Last < now - 2 * freq) continue;
                        int c = x.CountSince(now - freq);
                        if (c > bestCount || (c == bestCount && x.Last > best.Last)) { best = x; bestCount = c; }
                    }
                if (best == null)
                    foreach (Series x in _series)
                        if (x.Pid == pid && (best == null || x.Last > best.Last)) best = x;
                return best == null ? null : best.Copy();
            }
        }
    }

    internal sealed class HudPresentInfo
    {
        public const string ApiDxgi = "DXGI", ApiD3d9 = "D3D9";
        public string Api;
        public int Sync = -1, Model = -1;
        public bool Tearing;
        public long ApiAt, ModelAt, ModelSeenAnyAt;

        // Модели вывода D3DKMT_PRESENT_MODEL: 2 перенаправленный flip, 7 композиция, 9 flip — окно собирает DWM
        // обменом буферов; 1, 3, 4, 6 — копированием.
        public static bool FlipModel(int m) { return m == 2 || m == 7 || m == 9; }
        public static bool CopyModel(int m) { return m == 1 || m == 3 || m == 4 || m == 6; }

        public HudPresentInfo Clone() { return (HudPresentInfo)MemberwiseClone(); }
    }

    // ------------------------------------------------------------------ //
    //  Разбор EVENT_RECORD (x64)
    // ------------------------------------------------------------------ //
    internal static class HudPresentEvents
    {
        public static readonly Guid Dxgi = new Guid("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");
        public static readonly Guid D3d9 = new Guid("783aca0a-790e-4d7f-8451-aa850511c6b9");
        public static readonly Guid DxgKrnl = new Guid("802ec45a-1e99-4b83-9920-87c98277ba9d");

        public const ushort DxgiPresentStart = 42, D3d9PresentStart = 1, KrnlPresent = 184, KrnlPresentHistoryDetailed = 215;
        public const ulong DxgiKeywordEvents = 0x2, D3d9KeywordEvents = 0x2, KrnlKeywordPresent = 0x8000000;
        private const byte Flag32BitHeader = 0x20;
        private const uint DxgiPresentTest = 0x1, DxgiAllowTearing = 0x200;

        // Смещения EVENT_RECORD / EVENT_HEADER в 64-битном процессе. После ETW_BUFFER_CONTEXT (80) идёт сначала
        // ExtendedDataCount (84), и только потом UserDataLength (86) — сверено на настоящем .etl (тест «hud fps etl»).
        public const int OffFlags = 3, OffPid = 12, OffTime = 16, OffProvider = 24, OffId = 40,
                         OffUserDataLength = 86, OffUserData = 96, RecordSize = 112;

        // true — событие означает кадр; layer/key — какая это цепочка кадров процесса.
        public static bool Decode(IntPtr rec, out int pid, out int layer, out ulong key, out long qpc)
        {
            pid = 0; layer = 0; key = 0;
            qpc = Marshal.ReadInt64(rec, OffTime);
            ushort id = (ushort)Marshal.ReadInt16(rec, OffId);
            if (id != DxgiPresentStart && id != D3d9PresentStart && id != KrnlPresent && id != KrnlPresentHistoryDetailed) return false;
            byte[] g = new byte[16];
            Marshal.Copy(new IntPtr(rec.ToInt64() + OffProvider), g, 0, 16);
            Guid provider = new Guid(g);
            pid = Marshal.ReadInt32(rec, OffPid);
            int len = (ushort)Marshal.ReadInt16(rec, OffUserDataLength);
            IntPtr data = Marshal.ReadIntPtr(rec, OffUserData);
            int ptr = (Marshal.ReadByte(rec, OffFlags) & Flag32BitHeader) != 0 ? 4 : 8;

            if (provider == Dxgi && id == DxgiPresentStart)
            {
                // pIDXGISwapChain, Flags, SyncInterval. DXGI_PRESENT_TEST — проверка, кадр не выводится.
                if (data == IntPtr.Zero || len < ptr + 4) return false;
                key = ReadPointer(data, ptr);
                uint flags = (uint)Marshal.ReadInt32(data, ptr);
                if ((flags & DxgiPresentTest) != 0) return false;
                layer = HudPresentTracker.UserLayer;
                return true;
            }
            if (provider == D3d9 && id == D3d9PresentStart)
            {
                if (data == IntPtr.Zero || len < ptr) return false;
                key = ReadPointer(data, ptr);
                layer = HudPresentTracker.UserLayer;
                return true;
            }
            if (provider == DxgKrnl && (id == KrnlPresent || id == KrnlPresentHistoryDetailed))
            {
                // Два вида событий ядра — две разные цепочки: у одного процесса они не складываются.
                key = id;
                layer = HudPresentTracker.KernelLayer;
                return true;
            }
            return false;
        }

        // Подробности кадра для строк «API», «V-Sync», «Вывод»: DXGI Present Start — SyncInterval и разрыв кадра,
        // DxgKrnl 215 PresentHistoryDetailed — hAdapter, Token, Model (смещение по манифесту PresentMon, на
        // живой игре не сверено).
        public static void Details(IntPtr rec, HudPresentTracker tracker)
        {
            ushort id = (ushort)Marshal.ReadInt16(rec, OffId);
            if (id != DxgiPresentStart && id != D3d9PresentStart && id != KrnlPresentHistoryDetailed) return;
            byte[] g = new byte[16];
            Marshal.Copy(new IntPtr(rec.ToInt64() + OffProvider), g, 0, 16);
            Guid provider = new Guid(g);
            int pid = Marshal.ReadInt32(rec, OffPid);
            long qpc = Marshal.ReadInt64(rec, OffTime);
            int len = (ushort)Marshal.ReadInt16(rec, OffUserDataLength);
            IntPtr data = Marshal.ReadIntPtr(rec, OffUserData);
            int ptr = (Marshal.ReadByte(rec, OffFlags) & Flag32BitHeader) != 0 ? 4 : 8;
            if (data == IntPtr.Zero) return;
            if (provider == Dxgi && id == DxgiPresentStart && len >= ptr + 8)
            {
                uint flags = (uint)Marshal.ReadInt32(data, ptr);
                if ((flags & DxgiPresentTest) != 0) return;
                tracker.NoteApi(pid, HudPresentInfo.ApiDxgi, Marshal.ReadInt32(data, ptr + 4), (flags & DxgiAllowTearing) != 0, qpc);
            }
            else if (provider == D3d9 && id == D3d9PresentStart)
                tracker.NoteApi(pid, HudPresentInfo.ApiD3d9, -1, false, qpc);
            else if (provider == DxgKrnl && id == KrnlPresentHistoryDetailed && len >= ptr * 2 + 4)
                tracker.NoteModel(pid, Marshal.ReadInt32(data, ptr * 2), qpc);
        }

        private static ulong ReadPointer(IntPtr data, int size)
        {
            return size == 4 ? (uint)Marshal.ReadInt32(data) : (ulong)Marshal.ReadInt64(data);
        }
    }

    // ------------------------------------------------------------------ //
    //  Сессия ETW реального времени
    // ------------------------------------------------------------------ //
    internal sealed class HudEtwSession : IDisposable
    {
        public const string StateOff = "off", StateOk = "ok", StateNoRights = "no-rights", StateRelogon = "relogon",
                            State32Bit = "32-bit", StateError = "error";

        private const int ErrorAccessDenied = 5, ErrorAlreadyExists = 183;
        private const uint WnodeFlagTracedGuid = 0x00020000, RealTimeMode = 0x100, ControlStop = 1, EnableProvider = 1;
        private const uint ProcessTraceRealTime = 0x100, ProcessTraceEventRecord = 0x10000000;
        private const int PropsSize = 120, NameChars = 256, LogfileSize = 448;
        private static readonly ulong InvalidHandle = ulong.MaxValue;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void EventRecordCallback(IntPtr record);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int StartTraceW(out ulong handle, string name, IntPtr props);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int ControlTraceW(ulong handle, string name, IntPtr props, uint code);
        [DllImport("advapi32.dll")] private static extern int EnableTraceEx2(ulong handle, ref Guid provider, uint code, byte level,
                                                                             ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);
        [DllImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)] private static extern ulong OpenTrace(IntPtr logfile);
        [DllImport("advapi32.dll")] private static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll")] private static extern int CloseTrace(ulong handle);

        public readonly HudPresentTracker Tracker = new HudPresentTracker();
        public readonly string Name;
        private ulong _session, _trace = InvalidHandle;
        private IntPtr _logfile, _nameBuf;
        private EventRecordCallback _callback;      // держится полем: иначе сборщик мусора заберёт делегат у ETW
        private Thread _thread;
        public string State = StateOff;
        public int Error;
        public long EventsSeen;

        public HudEtwSession(string name) { Name = name; }

        public static string DefaultName()
        {
            string role = HudMode.IsHudProcess ? "HUD" : Process.GetCurrentProcess().ProcessName;
            return "SysDeck Frames (" + role + ")";
        }

        public bool Start()
        {
            if (IntPtr.Size != 8) { State = State32Bit; return false; }
            int rc = StartSession();
            if (rc == ErrorAlreadyExists)
            {
                // Сессия с этим именем осталась от упавшего процесса: у сессии реального времени без читателя нет
                // хозяина, сама она не закончится.
                StopByName(Name);
                rc = StartSession();
            }
            if (rc != 0)
            {
                Error = rc;
                State = rc == ErrorAccessDenied ? (HudPerfLog.Configured() ? StateRelogon : StateNoRights) : StateError;
                return false;
            }
            Guid dxgi = HudPresentEvents.Dxgi, d3d9 = HudPresentEvents.D3d9, krnl = HudPresentEvents.DxgKrnl;
            int e1 = EnableTraceEx2(_session, ref dxgi, EnableProvider, 5, HudPresentEvents.DxgiKeywordEvents, 0, 0, IntPtr.Zero);
            EnableTraceEx2(_session, ref d3d9, EnableProvider, 5, HudPresentEvents.D3d9KeywordEvents, 0, 0, IntPtr.Zero);
            EnableTraceEx2(_session, ref krnl, EnableProvider, 5, HudPresentEvents.KrnlKeywordPresent, 0, 0, IntPtr.Zero);
            if (e1 != 0) { Error = e1; State = e1 == ErrorAccessDenied ? StateNoRights : StateError; Stop(); return false; }

            _callback = OnEvent;
            _nameBuf = Marshal.StringToHGlobalUni(Name);
            _logfile = Logfile(_nameBuf, true, _callback);
            _trace = OpenTrace(_logfile);
            if (_trace == InvalidHandle)
            {
                Error = Marshal.GetLastWin32Error();
                State = StateError;
                Stop();
                return false;
            }
            ulong trace = _trace;
            _thread = new Thread(delegate()
            {
                try { ProcessTrace(new ulong[] { trace }, 1, IntPtr.Zero, IntPtr.Zero); }
                catch (Exception ex) { CapLog.Report(ex); }
            });
            _thread.IsBackground = true;
            _thread.Name = "HUD ETW";
            _thread.Start();
            State = StateOk;
            return true;
        }

        // EVENT_TRACE_LOGFILEW (x64, 448 байт): имя сессии или файла, режим, EventRecordCallback.
        private static IntPtr Logfile(IntPtr name, bool realTime, EventRecordCallback callback)
        {
            IntPtr p = Marshal.AllocHGlobal(LogfileSize);
            Zero(p, LogfileSize);
            Marshal.WriteIntPtr(p, realTime ? 8 : 0, name);                                     // LoggerName / LogFileName
            Marshal.WriteInt32(p, 28, unchecked((int)((realTime ? ProcessTraceRealTime : 0) | ProcessTraceEventRecord)));
            Marshal.WriteIntPtr(p, 424, Marshal.GetFunctionPointerForDelegate(callback));       // EventRecordCallback
            return p;
        }

        // Тот же читатель по файлу .etl — проверка раскладки структур на настоящих записях без прав на сессию.
        // Возвращает код ProcessTrace или -1, если файл не открылся.
        internal static int ReadEtl(string path, Action<IntPtr> onRecord)
        {
            if (IntPtr.Size != 8) return -1;
            EventRecordCallback cb = delegate(IntPtr rec) { try { onRecord(rec); } catch { } };
            IntPtr name = Marshal.StringToHGlobalUni(path);
            IntPtr lf = Logfile(name, false, cb);
            try
            {
                ulong h = OpenTrace(lf);
                if (h == InvalidHandle) return -1;
                try { return ProcessTrace(new ulong[] { h }, 1, IntPtr.Zero, IntPtr.Zero); }
                finally { CloseTrace(h); }
            }
            finally
            {
                Marshal.FreeHGlobal(lf);
                Marshal.FreeHGlobal(name);
                GC.KeepAlive(cb);
            }
        }

        private int StartSession()
        {
            IntPtr props = Props(Name);
            try
            {
                ulong h;
                int rc = StartTraceW(out h, Name, props);
                if (rc == 0) _session = h;
                return rc;
            }
            finally { Marshal.FreeHGlobal(props); }
        }

        private static IntPtr Props(string name)
        {
            int size = PropsSize + NameChars * 2;
            IntPtr p = Marshal.AllocHGlobal(size);
            Zero(p, size);
            Marshal.WriteInt32(p, 0, size);                                  // Wnode.BufferSize
            Marshal.WriteInt32(p, 40, 1);                                    // Wnode.ClientContext = QPC
            Marshal.WriteInt32(p, 44, unchecked((int)WnodeFlagTracedGuid));  // Wnode.Flags
            Marshal.WriteInt32(p, 48, 16);                                   // BufferSize, КБ: мелкие буферы — меньше задержка
            Marshal.WriteInt32(p, 52, 4);                                    // MinimumBuffers
            Marshal.WriteInt32(p, 56, 64);                                   // MaximumBuffers
            Marshal.WriteInt32(p, 64, unchecked((int)RealTimeMode));         // LogFileMode
            Marshal.WriteInt32(p, 68, 1);                                    // FlushTimer, с
            Marshal.WriteInt32(p, 116, PropsSize);                           // LoggerNameOffset
            return p;
        }

        private static void StopByName(string name)
        {
            IntPtr props = Props(name);
            try { ControlTraceW(0, name, props, ControlStop); }
            finally { Marshal.FreeHGlobal(props); }
        }

        private void OnEvent(IntPtr rec)
        {
            try
            {
                EventsSeen++;
                int pid, layer; ulong key; long qpc;
                if (HudPresentEvents.Decode(rec, out pid, out layer, out key, out qpc))
                {
                    Tracker.Present(pid, layer, key, qpc);
                    HudPresentEvents.Details(rec, Tracker);
                }
                else Tracker.Seen(qpc);
            }
            catch { }
        }

        private void Stop()
        {
            if (_trace != InvalidHandle) { CloseTrace(_trace); _trace = InvalidHandle; }
            if (_session != 0)
            {
                IntPtr props = Props(Name);
                try { ControlTraceW(_session, null, props, ControlStop); }
                finally { Marshal.FreeHGlobal(props); }
                _session = 0;
            }
        }

        public void Dispose()
        {
            Stop();
            if (_thread != null) { _thread.Join(3000); _thread = null; }
            if (_logfile != IntPtr.Zero) { Marshal.FreeHGlobal(_logfile); _logfile = IntPtr.Zero; }
            if (_nameBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_nameBuf); _nameBuf = IntPtr.Zero; }
            if (State == StateOk) State = StateOff;
        }

        private static void Zero(IntPtr p, int size)
        {
            Marshal.Copy(new byte[size], 0, p, size);
        }
    }

    // ------------------------------------------------------------------ //
    //  Источник строк «Кадры»
    // ------------------------------------------------------------------ //
    internal sealed class HudFpsSource : HudSource
    {
        private const int RetryMs = 30000;
        private HudEtwSession _etw;
        private long _retryAt;
        private int _namePid;
        private string _name;

        // Последнее состояние сессии этого процесса — для строки состояния на странице «Оверлей».
        public static string LastState = HudEtwSession.StateOff;

        public HudFpsSource() { PeriodMs = 250; }

        public override string Name { get { return "ETW"; } }

        public string State { get { return _etw != null ? _etw.State : LastState; } }

        // Метки кадров последнего замера (QPC) — для записи лагов: она сама отбрасывает уже записанные.
        public long[] LastFrames;
        public int LastFramesPid;
        public long LastFreq;

        public override void Collect(HudFrame f)
        {
            if (_etw == null || _etw.State != HudEtwSession.StateOk)
            {
                if (Environment.TickCount < _retryAt && _etw != null) return;
                if (_etw != null) _etw.Dispose();
                _etw = new HudEtwSession(HudEtwSession.DefaultName());
                _etw.Start();
                LastState = _etw.State;
                _retryAt = Environment.TickCount + RetryMs;
                if (_etw.State != HudEtwSession.StateOk) return;
            }
            Put(f, _etw.Tracker, HudProcTree.ForegroundPid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency, HudProcTree.Parents());
        }

        // Отдельно от сессии — для тестов на синтетических кадрах (дерево процессов пустое).
        internal void Put(HudFrame f, HudPresentTracker tracker, int pid, long qpcNow, long freq)
        {
            Put(f, tracker, pid, qpcNow, freq, new Dictionary<int, int>());
        }

        internal void Put(HudFrame f, HudPresentTracker tracker, int pid, long qpcNow, long freq, IDictionary<int, int> parents)
        {
            if (pid <= 0) return;
            // «Сейчас» — метка самого свежего события, если оно недавнее: буферы ETW приходят с задержкой, и без
            // этого последняя секунда всегда выглядела бы полупустой.
            long latest = tracker.Latest;
            long now = latest > 0 && qpcNow - latest < freq * 3 / 2 ? latest : qpcNow;
            long drop = now - (long)(HudFrameMath.StutterWindowSec * freq);
            long[] ts = tracker.Pick(pid, now, freq, drop);
            // Браузеры и Electron выводят кадры не из процесса окна, а из дочернего GPU-процесса — берём самого
            // частого из потомков окна.
            int framesPid = pid;
            if (ts == null) ts = PickDescendant(tracker, pid, now, freq, drop, parents, out framesPid);
            if (ts == null) return;
            LastFrames = ts;
            LastFramesPid = framesPid;
            LastFreq = freq;
            HudFpsResult r = HudFrameMath.Compute(ts, ts.Length, now, freq);
            if (!HudFormat.Valid(r.Fps)) return;
            f.Put("fps", HudKind.Fps, r.Fps);
            HudValue ft = new HudValue("fps.frametime", HudKind.Ms, r.FrameMs);
            ft.Series = r.Series;
            f.Put(ft);
            f.Put("fps.low1", HudKind.Fps, r.Low1);
            f.Put("fps.low01", HudKind.Fps, r.Low01);
            HudValue st = new HudValue("fps.stutter", HudKind.Number, r.StuttersPerMin);
            st.Unit = Tr.S("/мин", "/min");
            f.Put(st);
            f.PutText("fps.app", ProcessName(pid));
            PutPresent(f, tracker.Info(framesPid), now, freq);
        }

        // Строки «API», «V-Sync», «Вывод». Сведения старше двух секунд не показываются — игра могла сменить режим.
        internal static void PutPresent(HudFrame f, HudPresentInfo info, long now, long freq)
        {
            bool api = info != null && info.Api != null && now - info.ApiAt < 2 * freq;
            if (!api) f.PutText("fps.api", Tr.S("OpenGL / Vulkan / другое", "OpenGL / Vulkan / other"));
            else f.PutText("fps.api", info.Api == HudPresentInfo.ApiDxgi ? "DXGI (Direct3D 10–12)" : "Direct3D 9");
            if (api && info.Api == HudPresentInfo.ApiDxgi && info.Sync >= 0)
                f.PutText("fps.vsync", info.Sync > 0
                    ? (info.Sync == 1 ? Tr.S("вкл", "on") : Tr.S("вкл, каждый ", "on, every ") + info.Sync.ToString(CultureInfo.InvariantCulture) + Tr.S("-й", "th"))
                    : info.Tearing ? Tr.S("выкл, с разрывами", "off, tearing allowed") : Tr.S("выкл", "off"));
            if (info == null || info.ModelSeenAnyAt <= 0 || now - info.ModelSeenAnyAt > 10 * freq) return;
            bool composed = info.Model >= 0 && now - info.ModelAt < 2 * freq;
            if (!composed) f.PutText("fps.presentmode", Tr.S("напрямую, без DWM", "direct, no DWM"));
            else if (HudPresentInfo.FlipModel(info.Model)) f.PutText("fps.presentmode", Tr.S("через DWM, обмен", "via DWM, flip"));
            else if (HudPresentInfo.CopyModel(info.Model)) f.PutText("fps.presentmode", Tr.S("через DWM, копирование", "via DWM, copy"));
        }

        internal static long[] PickDescendant(HudPresentTracker tracker, int pid, long now, long freq, long drop, IDictionary<int, int> parents)
        {
            int ignored;
            return PickDescendant(tracker, pid, now, freq, drop, parents, out ignored);
        }

        internal static long[] PickDescendant(HudPresentTracker tracker, int pid, long now, long freq, long drop, IDictionary<int, int> parents, out int bestPid)
        {
            bestPid = pid;
            long[] best = null;
            int bestCount = -1;
            foreach (int child in tracker.Pids())
            {
                if (child == pid || !IsDescendant(child, pid, parents)) continue;
                long[] ts = tracker.Pick(child, now, freq, drop);
                if (ts == null) continue;
                int c = 0;
                for (int i = ts.Length - 1; i >= 0 && ts[i] >= now - freq; i--) c++;
                if (c > bestCount) { best = ts; bestCount = c; bestPid = child; }
            }
            return best;
        }

        // Не глубже четырёх поколений: GPU-процесс Chromium — прямой потомок, у Electron бывает промежуточный.
        internal static bool IsDescendant(int pid, int ancestor, IDictionary<int, int> parents)
        {
            int cur = pid;
            for (int depth = 0; depth < 4; depth++)
            {
                int parent;
                if (!parents.TryGetValue(cur, out parent) || parent <= 0 || parent == cur) return false;
                if (parent == ancestor) return true;
                cur = parent;
            }
            return false;
        }

        private string ProcessName(int pid)
        {
            if (pid != _namePid)
            {
                _namePid = pid;
                try { using (Process p = Process.GetProcessById(pid)) _name = p.ProcessName; }
                catch { _name = pid.ToString(); }
            }
            return _name;
        }

        public override void Dispose()
        {
            if (_etw != null) { _etw.Dispose(); _etw = null; }
        }
    }

    // ------------------------------------------------------------------ //
    //  Группа «Пользователи журналов производительности»
    // ------------------------------------------------------------------ //
    internal static class HudPerfLog
    {
        public const string GroupSid = "S-1-5-32-559";
        private const int ErrorMemberInAlias = 1378, WtsUserName = 5, WtsDomainName = 7, LgIncludeIndirect = 1;

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetLocalGroupAddMembers(string server, string group, int level, ref IntPtr members, int count);
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetUserGetLocalGroups(string server, string user, int level, int flags, out IntPtr buf,
                                                        int prefMax, out int read, out int total);
        [DllImport("netapi32.dll")] private static extern int NetApiBufferFree(IntPtr buf);
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)]
        private static extern bool WTSQuerySessionInformationW(IntPtr server, int session, int info, out IntPtr buf, out int bytes);
        [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr buf);

        // Группа уже в токене этого процесса — сессию ETW можно заводить без прав.
        public static bool InToken()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    foreach (IdentityReference g in id.Groups)
                        if (g.Value == GroupSid) return true;
            }
            catch { }
            return false;
        }

        public static string GroupName()
        {
            string full = new SecurityIdentifier(GroupSid).Translate(typeof(NTAccount)).Value;
            int slash = full.IndexOf('\\');
            return slash >= 0 ? full.Substring(slash + 1) : full;
        }

        // Человек в группе по данным учётной записи (после добавления — ещё до нового входа).
        public static bool Configured()
        {
            IntPtr buf = IntPtr.Zero;
            try
            {
                string group = GroupName();
                int read, total;
                if (NetUserGetLocalGroups(null, Environment.UserDomainName + "\\" + Environment.UserName, 0, LgIncludeIndirect,
                                          out buf, -1, out read, out total) != 0) return false;
                for (int i = 0; i < read; i++)
                {
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, i * IntPtr.Size));
                    if (string.Equals(name, group, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            finally { if (buf != IntPtr.Zero) NetApiBufferFree(buf); }
            return false;
        }

        // Только из помощника с правами (--elevated-job «perflog»). Добавляется человек, вошедший в этот сеанс Windows, —
        // не учётная запись, чьим паролем подтвердили UAC, и не имя из файла задания. null — успех.
        public static string AddInteractiveUser()
        {
            string account = SessionAccount(Process.GetCurrentProcess().SessionId);
            if (string.IsNullOrEmpty(account)) return Tr.S("не удалось узнать пользователя сеанса", "could not determine the session user");
            IntPtr name = Marshal.StringToHGlobalUni(account);
            try
            {
                int rc = NetLocalGroupAddMembers(null, GroupName(), 3, ref name, 1);
                return rc == 0 || rc == ErrorMemberInAlias ? null : new Win32Exception(rc).Message + " (" + rc + ")";
            }
            finally { Marshal.FreeHGlobal(name); }
        }

        private static string SessionAccount(int session)
        {
            string user = WtsString(session, WtsUserName), domain = WtsString(session, WtsDomainName);
            if (string.IsNullOrEmpty(user)) return null;
            return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
        }

        private static string WtsString(int session, int info)
        {
            IntPtr buf;
            int bytes;
            if (!WTSQuerySessionInformationW(IntPtr.Zero, session, info, out buf, out bytes)) return null;
            try { return Marshal.PtrToStringUni(buf); }
            finally { WTSFreeMemory(buf); }
        }
    }
}
