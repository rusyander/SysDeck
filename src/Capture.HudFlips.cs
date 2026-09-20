// SysDeck — кадры НА ЭКРАНЕ, а не в вызовах Present(). Строка «Кадр» и её пила считаются по событиям
// Microsoft-Windows-DXGI Present_Start: это момент, когда игра ОТДАЛА кадр, и он гуляет на несколько миллисекунд
// сам по себе, даже когда на экране всё ровно. Здесь считается второе время — когда кадр реально ушёл на развёртку.
//
// Цепочек две, и работает та, по которой кадр реально идёт.
//
// 1. Кадр собирает DWM (обычное окно и почти всякая игра без монопольного полноэкранного режима).
//    PresentHistory (171) и PresentHistoryDetailed (215) приходят в контексте самой программы и несут Token;
//    PresentHistory Info (172) с тем же токеном означает, что кадр принят на вывод. Только так и можно узнать,
//    чей это кадр: события Flip в этом режиме выдаёт dwm.exe, и по ним все кадры достались бы ему одному.
//    Проверено живым захватом 20.09.2026: у игры 713 вызовов Present и 712 отработавших токенов за шесть секунд,
//    токен разрешился в процесс во всех 1889 случаях.
//
// 2. Кадр идёт на развёртку напрямую (монопольный полноэкранный режим) — цепочка из трёх звеньев:
//   Flip (168) / FlipMultiPlaneOverlay (252) — процесс отдал кадр на вывод. Номера отправки в этих событиях НЕТ,
//     зато есть VidPnSourceId и, главное, идентификатор процесса в заголовке: отсюда берётся «чей это экран».
//   MMIOFlip (116) / MMIOFlipMultiPlaneOverlay (259) — кадр запрограммирован в контроллер; здесь появляется
//     FlipSubmitSequence (у 116 — 32 бита, у 259 — 64) и тот же VidPnSourceId.
//   VSyncDPC (17) / VSyncDPCMultiPlane (273) — номер отработал на вертикальном гашении: кадр на экране.
//     У 17 номер лежит в FlipFenceId (64 бита), у 273 — в FlipSubmitSequence.
//
// Идентификаторы и поля сверены с живым манифестом этой машины (Get-WinEvent -ListProvider Microsoft-Windows-DxgKrnl),
// все события под уже включённым ключевым словом Present (0x8000000). Смещения полей НЕ зашиты в код: их по именам
// выдаёт TDH (tdh.dll) один раз на событие и кладёт в кэш. Не нашлось нужного поля — метрика просто не появляется;
// врать цифрой нельзя.
//
// В какой половине FlipFenceId лежит номер отправки, документация не обещает, поэтому проверяются обе: совпала
// ровно одна — она и есть; совпали обе — случай неразличим, и кадр не засчитывается.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SysDeck.Capture
{
    // ------------------------------------------------------------------ //
    //  Смещения полей события по именам — через TDH, один раз на событие
    // ------------------------------------------------------------------ //
    internal static class HudEventFields
    {
        [DllImport("tdh.dll")]
        private static extern int TdhGetEventInformation(IntPtr record, uint contextCount, IntPtr context, IntPtr buffer, ref uint size);

        private const int InfoPropertyCount = 100, InfoFirstProperty = 112, PropertyInfoSize = 24;
        private const int FlagStruct = 0x4, FlagParamCount = 0x1, FlagParamLength = 0x2, FlagParamFixedCount = 0x10;
        private const int MaxCached = 64;

        internal sealed class Field { public int Offset, Size; }

        internal sealed class Layout
        {
            public readonly Dictionary<string, Field> Fields = new Dictionary<string, Field>(StringComparer.OrdinalIgnoreCase);
            public bool Ok;
        }

        private static readonly Dictionary<long, Layout> Cache = new Dictionary<long, Layout>();

        // Размер значения по TDH_INTYPE. 0 — переменная длина: дальше по записи идти нельзя.
        private static int SizeOf(int inType, bool pointer32)
        {
            switch (inType)
            {
                case 3: case 4: return 1;                      // INT8, UINT8
                case 5: case 6: return 2;                      // INT16, UINT16
                case 7: case 8: case 11: case 20: return 4;    // INT32, UINT32, FLOAT, HEXINT32
                case 13: return 4;                             // BOOLEAN — это Win32 BOOL, четыре байта
                case 9: case 10: case 12: case 17: case 21: return 8;   // INT64, UINT64, DOUBLE, FILETIME, HEXINT64
                case 15: case 18: return 16;                   // GUID, SYSTEMTIME
                case 16: return pointer32 ? 4 : 8;             // POINTER
                default: return 0;                             // строки, BINARY, SID и всё незнакомое
            }
        }

        public static Layout For(IntPtr record, Guid provider, ushort id, byte version, bool pointer32)
        {
            long key = ((long)provider.GetHashCode() << 24) ^ ((long)id << 8) ^ version;
            Layout cached;
            lock (Cache) if (Cache.TryGetValue(key, out cached)) return cached;
            Layout layout = Read(record, pointer32);
            lock (Cache)
            {
                if (Cache.Count >= MaxCached) Cache.Clear();
                Cache[key] = layout;
            }
            return layout;
        }

        private static Layout Read(IntPtr record, bool pointer32)
        {
            IntPtr buf = IntPtr.Zero;
            try
            {
                uint size = 0;
                TdhGetEventInformation(record, 0, IntPtr.Zero, IntPtr.Zero, ref size);
                if (size == 0 || size > 1024 * 256) return new Layout();
                buf = Marshal.AllocHGlobal((int)size);
                if (TdhGetEventInformation(record, 0, IntPtr.Zero, buf, ref size) != 0) return new Layout();
                return Walk(buf, size, pointer32);
            }
            catch { return new Layout(); }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }

        // Разбор TRACE_EVENT_INFO: поля идут в записи подряд без выравнивания, поэтому смещение каждого — сумма
        // размеров предыдущих. Первое поле переменной длины обрывает разбор: дальше смещения уже не посчитать.
        internal static Layout Walk(IntPtr buf, uint size, bool pointer32)
        {
            Layout layout = new Layout();
            try
            {
                int count = Marshal.ReadInt32(buf, InfoPropertyCount);
                if (count <= 0 || count > 256) return layout;
                int offset = 0;
                for (int i = 0; i < count; i++)
                {
                    int at = InfoFirstProperty + i * PropertyInfoSize;
                    if (at + PropertyInfoSize > size) break;
                    int flags = Marshal.ReadInt32(buf, at);
                    // Вложенная структура или длина/количество из другого поля — дальше смещения не посчитать.
                    if ((flags & (FlagStruct | FlagParamCount | FlagParamLength)) != 0) break;
                    int nameOffset = Marshal.ReadInt32(buf, at + 4);
                    int inType = (ushort)Marshal.ReadInt16(buf, at + 8);
                    int elements = (ushort)Marshal.ReadInt16(buf, at + 16);
                    if (elements == 0) elements = 1;
                    if ((flags & FlagParamFixedCount) == 0 && elements > 1024) break;
                    int one = SizeOf(inType, pointer32);
                    if (one == 0) break;
                    string name = nameOffset > 0 && nameOffset < size ? Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + nameOffset)) : null;
                    if (!string.IsNullOrEmpty(name) && !layout.Fields.ContainsKey(name))
                    {
                        Field f = new Field();
                        f.Offset = offset; f.Size = one;
                        layout.Fields[name] = f;
                    }
                    offset += one * elements;
                }
                layout.Ok = layout.Fields.Count > 0;
            }
            catch { }
            return layout;
        }

        // Значение поля как беззнаковое целое; false — поля нет или запись короче.
        public static bool Value(Layout layout, string name, IntPtr data, int length, out ulong value)
        {
            value = 0;
            Field f;
            if (layout == null || !layout.Ok || data == IntPtr.Zero || !layout.Fields.TryGetValue(name, out f)) return false;
            if (f.Offset + f.Size > length) return false;
            switch (f.Size)
            {
                case 1: value = (byte)Marshal.ReadByte(data, f.Offset); return true;
                case 2: value = (ushort)Marshal.ReadInt16(data, f.Offset); return true;
                case 4: value = (uint)Marshal.ReadInt32(data, f.Offset); return true;
                case 8: value = (ulong)Marshal.ReadInt64(data, f.Offset); return true;
                default: return false;
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  Кадры на экране: очередь вывода -> вертикальное гашение
    // ------------------------------------------------------------------ //
    internal sealed class HudFlipTracker
    {
        public const ushort Flip = 168, FlipMpo = 252, MmioFlip = 116, MmioFlipMpo = 259, VSyncDpc = 17, VSyncDpcMpo = 273;
        public const ushort History = 171, HistoryInfo = 172, HistoryDetailed = 215;
        private const int MaxPending = 512, MaxPids = 64, MaxSources = 32, Capacity = 1024;

        private struct Pending { public int Pid; public long At; }

        private sealed class Screen
        {
            public readonly long[] Ring = new long[Capacity];
            public int Head, Count;
            public long Last;

            public void Push(long t)
            {
                if (Count > 0 && t <= Last) return;
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
        }

        private readonly object _gate = new object();
        private readonly Dictionary<uint, Pending> _pending = new Dictionary<uint, Pending>();
        private readonly List<uint> _order = new List<uint>();
        private readonly Dictionary<int, Screen> _screens = new Dictionary<int, Screen>();
        private readonly Dictionary<uint, int> _owner = new Dictionary<uint, int>();   // VidPnSourceId -> чей это вывод
        private readonly Dictionary<ulong, int> _tokens = new Dictionary<ulong, int>();   // Token -> кто отдал кадр
        private readonly List<ulong> _tokenOrder = new List<ulong>();
        private readonly Dictionary<int, Screen> _composed = new Dictionary<int, Screen>();

        public long Matched, Flips, Queued, Shown;

        // Кто отдал кадр на этот выход. В событии Flip номера отправки нет, зато есть процесс в заголовке, —
        // а в MMIOFlip наоборот: номер есть, а процесс уже не тот (кадр программирует планировщик ядра).
        public void Owner(uint source, int pid)
        {
            if (pid <= 0) return;
            lock (_gate)
            {
                if (!_owner.ContainsKey(source) && _owner.Count >= MaxSources) _owner.Clear();
                _owner[source] = pid;
            }
        }

        public void Submit(uint source, ulong sequence, long qpc)
        {
            if (sequence == 0) return;
            uint key = (uint)sequence;
            lock (_gate)
            {
                int pid;
                if (!_owner.TryGetValue(source, out pid) || pid <= 0) return;
                Flips++;
                if (!_pending.ContainsKey(key)) _order.Add(key);
                Pending p = new Pending();
                p.Pid = pid; p.At = qpc;
                _pending[key] = p;
                while (_order.Count > MaxPending)
                {
                    _pending.Remove(_order[0]);
                    _order.RemoveAt(0);
                }
            }
        }

        public void Displayed(ulong fenceId, long qpc)
        {
            lock (_gate)
            {
                uint low = (uint)fenceId, high = (uint)(fenceId >> 32);
                bool lowHit = low != 0 && _pending.ContainsKey(low), highHit = high != 0 && _pending.ContainsKey(high);
                // Обе половины — ожидающие отправки: какая из них отработала, не различить, и кадр пропускается.
                if (lowHit == highHit) return;
                uint key = lowHit ? low : high;
                Pending p;
                if (!_pending.TryGetValue(key, out p)) return;
                _pending.Remove(key);
                _order.Remove(key);
                Matched++;
                Mark(_screens, p.Pid, qpc);
            }
        }

        // PresentHistory (171) и PresentHistoryDetailed (215) приходят в контексте процесса, который отдал кадр:
        // отсюда известно, чей это токен. Сами токены — адреса, Windows выдаёт их заново по кругу (на живом
        // захвате их было шесть на четыре программы), поэтому запись перетирается, а не копится.
        public void Queue(ulong token, int pid)
        {
            if (token == 0 || pid <= 0) return;
            lock (_gate)
            {
                if (!_tokens.ContainsKey(token)) _tokenOrder.Add(token);
                _tokens[token] = pid;
                Queued++;
                while (_tokenOrder.Count > MaxPending)
                {
                    _tokens.Remove(_tokenOrder[0]);
                    _tokenOrder.RemoveAt(0);
                }
            }
        }

        // PresentHistory Info (172): токен отработал — кадр принят на вывод. Это единственный путь для кадров,
        // которые собирает DWM: там событие Flip выдаёт сам dwm.exe, и по нему нельзя сказать, чей это кадр.
        public void Retire(ulong token, long qpc)
        {
            if (token == 0) return;
            lock (_gate)
            {
                int pid;
                if (!_tokens.TryGetValue(token, out pid)) return;
                _tokens.Remove(token);
                _tokenOrder.Remove(token);
                Shown++;
                Mark(_composed, pid, qpc);
            }
        }

        private static void Mark(Dictionary<int, Screen> where, int pid, long qpc)
        {
            Screen s;
            if (!where.TryGetValue(pid, out s))
            {
                if (where.Count >= MaxPids) where.Clear();
                s = new Screen();
                where[pid] = s;
            }
            s.Push(qpc);
        }

        // Метки вывода кадров процесса на экран; null — про этот процесс ничего не собрано. Два источника не
        // смешиваются: сложить их означало бы посчитать один кадр дважды, поэтому берётся тот, где данные есть.
        public long[] Pick(int pid)
        {
            lock (_gate)
            {
                Screen s;
                if (_composed.TryGetValue(pid, out s) && s.Count >= 2) return s.Copy();
                if (_screens.TryGetValue(pid, out s) && s.Count >= 2) return s.Copy();
                return null;
            }
        }

        public void Forget(int pid) { lock (_gate) { _screens.Remove(pid); _composed.Remove(pid); } }
    }

    // ------------------------------------------------------------------ //
    //  Разбор событий вывода
    // ------------------------------------------------------------------ //
    internal static class HudFlipEvents
    {
        // true — событие разобрано и учтено.
        public static bool Handle(IntPtr rec, HudFlipTracker flips)
        {
            ushort id = (ushort)Marshal.ReadInt16(rec, HudPresentEvents.OffId);
            if (id != HudFlipTracker.Flip && id != HudFlipTracker.FlipMpo && id != HudFlipTracker.MmioFlip
                && id != HudFlipTracker.MmioFlipMpo && id != HudFlipTracker.VSyncDpc && id != HudFlipTracker.VSyncDpcMpo
                && id != HudFlipTracker.History && id != HudFlipTracker.HistoryInfo && id != HudFlipTracker.HistoryDetailed)
                return false;
            byte[] g = new byte[16];
            Marshal.Copy(new IntPtr(rec.ToInt64() + HudPresentEvents.OffProvider), g, 0, 16);
            Guid provider = new Guid(g);
            if (provider != HudPresentEvents.DxgKrnl) return false;
            int len = (ushort)Marshal.ReadInt16(rec, HudPresentEvents.OffUserDataLength);
            IntPtr data = Marshal.ReadIntPtr(rec, HudPresentEvents.OffUserData);
            if (data == IntPtr.Zero || len <= 0) return false;
            bool pointer32 = (Marshal.ReadByte(rec, HudPresentEvents.OffFlags) & 0x20) != 0;
            byte version = Marshal.ReadByte(rec, HudPresentEvents.OffId + 2);
            HudEventFields.Layout layout = HudEventFields.For(rec, provider, id, version, pointer32);
            if (layout == null || !layout.Ok) return false;
            long qpc = Marshal.ReadInt64(rec, HudPresentEvents.OffTime);
            ulong v, source;
            if (id == HudFlipTracker.History || id == HudFlipTracker.HistoryDetailed)
            {
                if (!HudEventFields.Value(layout, "Token", data, len, out v)) return false;
                flips.Queue(v, Marshal.ReadInt32(rec, HudPresentEvents.OffPid));
                return true;
            }
            if (id == HudFlipTracker.HistoryInfo)
            {
                if (!HudEventFields.Value(layout, "Token", data, len, out v)) return false;
                flips.Retire(v, qpc);
                return true;
            }
            if (id == HudFlipTracker.Flip || id == HudFlipTracker.FlipMpo)
            {
                if (!HudEventFields.Value(layout, "VidPnSourceId", data, len, out source)) return false;
                flips.Owner((uint)source, Marshal.ReadInt32(rec, HudPresentEvents.OffPid));
                return true;
            }
            if (id == HudFlipTracker.MmioFlip || id == HudFlipTracker.MmioFlipMpo)
            {
                if (!HudEventFields.Value(layout, "VidPnSourceId", data, len, out source)
                    || !HudEventFields.Value(layout, "FlipSubmitSequence", data, len, out v)) return false;
                flips.Submit((uint)source, v, qpc);
                return true;
            }
            // VSyncDPC несёт номер в FlipFenceId, многоплоскостной вариант — в FlipSubmitSequence.
            if (!HudEventFields.Value(layout, "FlipFenceId", data, len, out v)
                && !HudEventFields.Value(layout, "FlipSubmitSequence", data, len, out v)) return false;
            flips.Displayed(v, qpc);
            return true;
        }
    }
}
