// SysDeck — «максимальное состояние процессора» активной схемы питания.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Тот же параметр, что в «Панель управления → Электропитание → Изменить дополнительные параметры питания →
// Управление питанием процессора → Максимальное состояние процессора» (PROCTHROTTLEMAX). Две кнопки в шапке
// окна ставят 99 % («пониженная» — на большинстве ПК это отключает турбо-частоты и заметно снижает нагрев) или
// 100 %. Значение пишется и «от сети», и «от батареи», затем схема активируется заново — так Windows применяет
// его сразу. Хранит значение сама Windows (в схеме питания), поэтому выбор переживает перезапуск программы и ПК;
// подсветка кнопки всегда берётся из текущего значения, а не из собственных настроек.
// Прав администратора не нужно: схемы питания пользователь меняет и в панели управления без UAC.
using System;
using System.Runtime.InteropServices;

namespace SysDeck
{
    internal enum CpuPowerMode { None, Reduced, Max }

    internal sealed class CpuPowerState
    {
        public bool Ok;
        public string Error;
        public int Ac = -1, Dc = -1;      // проценты; -1 — не прочитано
        public bool OnBattery;

        public int Current { get { return OnBattery && Dc >= 0 ? Dc : Ac; } }
        public CpuPowerMode Mode { get { return Ok ? CpuPower.ModeOf(Current) : CpuPowerMode.None; } }
    }

    internal static class CpuPower
    {
        public const int ReducedPercent = 99, MaxPercent = 100;

        private static readonly Guid SubProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        private static readonly Guid ThrottleMax = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");

        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);
        [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, uint value);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        // Ровно 99 — «пониженная», ровно 100 — «максимальная»; любое другое значение ни одной кнопке не отвечает.
        public static CpuPowerMode ModeOf(int percent)
        {
            if (percent == ReducedPercent) return CpuPowerMode.Reduced;
            if (percent == MaxPercent) return CpuPowerMode.Max;
            return CpuPowerMode.None;
        }

        public static int PercentOf(CpuPowerMode mode)
        {
            return mode == CpuPowerMode.Reduced ? ReducedPercent : mode == CpuPowerMode.Max ? MaxPercent : -1;
        }

        public static CpuPowerState Read()
        {
            CpuPowerState st = new CpuPowerState();
            SYSTEM_POWER_STATUS ps;
            st.OnBattery = GetSystemPowerStatus(out ps) && ps.ACLineStatus == 0;
            Guid scheme;
            uint err = ActiveScheme(out scheme);
            if (err != 0) { st.Error = Describe(err); return st; }
            Guid sub = SubProcessor, set = ThrottleMax;
            uint ac, dc;
            err = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out ac);
            if (err != 0) { st.Error = Describe(err); return st; }
            st.Ac = (int)ac;
            if (PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out dc) == 0) st.Dc = (int)dc;
            st.Ok = true;
            return st;
        }

        // null — записано и применено; иначе текст ошибки.
        public static string Write(int percent)
        {
            if (percent < 0 || percent > 100) return "percent out of range";
            Guid scheme;
            uint err = ActiveScheme(out scheme);
            if (err != 0) return Describe(err);
            Guid sub = SubProcessor, set = ThrottleMax;
            err = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, (uint)percent);
            if (err != 0) return Describe(err);
            err = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, (uint)percent);
            if (err != 0) return Describe(err);
            // Запись в активную схему вступает в силу только после повторной активации.
            err = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            return err == 0 ? null : Describe(err);
        }

        private static uint ActiveScheme(out Guid scheme)
        {
            scheme = Guid.Empty;
            IntPtr p;
            uint err = PowerGetActiveScheme(IntPtr.Zero, out p);
            if (err != 0) return err;
            try { scheme = (Guid)Marshal.PtrToStructure(p, typeof(Guid)); }
            finally { LocalFree(p); }
            return 0;
        }

        private static string Describe(uint err)
        {
            return new System.ComponentModel.Win32Exception((int)err).Message + " (" + err + ")";
        }
    }
}
