// SysDeck — ограничитель кадров драйвера NVIDIA (тот же, что «Макс. частота кадров» в Панели управления
// NVIDIA). Пишется в профиль драйвера через NVAPI DRS: общий профиль (все игры) или профиль конкретного exe.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// В игру ничего не внедряется — кадры держит сам драйвер, как при настройке из его панели. Запись в базу профилей
// драйвер разрешает не всем: без прав администратора SaveSettings бывает отклонён (NVAPI_INVALID_USER_PRIVILEGE),
// тогда страница повторяет запись через помощник --elevated-job (вид «nvlimit»: номер = кадры, строка = имя exe).
//
// Структуры NVAPI (nvapi.h R535): NVDRS_SETTING v1 — 12320 байт, settingId @4100, settingType @4104,
// currentValue @8220; NVDRS_APPLICATION v1 — 12296 байт; NVDRS_PROFILE v1 — 4116 байт. Функции берутся через
// nvapi_QueryInterface по номерам.
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SysDeck
{
    internal static class NvFrameLimit
    {
        public const uint FrlFpsId = 0x10835002;              // FRL_FPS_ID — «Max Frame Rate», 0 = выкл
        public const int MaxFps = 1000;
        private const int SettingSize = 12320, SettingIdOff = 4100, SettingTypeOff = 4104, SettingCurrentOff = 8220;
        private const int AppSize = 12296, ProfileSize = 4116, NameChars = 2048;
        private const int NvapiOk = 0, NvapiInvalidUserPrivilege = -137, NvapiSettingNotFound = -160;
        public const string ProfilePrefix = "WPC ";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnVoid();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnOutHandle(out IntPtr h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnHandle(IntPtr h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnHandleOut(IntPtr h, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnFindApp(IntPtr session, IntPtr appName, out IntPtr profile, IntPtr app);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnSetSetting(IntPtr session, IntPtr profile, IntPtr setting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnGetSetting(IntPtr session, IntPtr profile, uint id, IntPtr setting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnDeleteSetting(IntPtr session, IntPtr profile, uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnCreateProfile(IntPtr session, IntPtr info, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnFindProfile(IntPtr session, IntPtr name, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnCreateApp(IntPtr session, IntPtr profile, IntPtr app);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnDeleteProfile(IntPtr session, IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnProfileInfo(IntPtr session, IntPtr profile, IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FnQuery(uint id);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private static readonly object Gate = new object();
        private static FnQuery _query;
        private static bool _tried, _initialized;

        // Путь — только System32: библиотека грузится и в повышенном помощнике, искать её по PATH нельзя.
        private static bool Load()
        {
            if (_tried) return _initialized;
            _tried = true;
            try
            {
                string dll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), IntPtr.Size == 8 ? "nvapi64.dll" : "nvapi.dll");
                if (!File.Exists(dll)) return false;
                IntPtr module = LoadLibraryW(dll);
                if (module == IntPtr.Zero) return false;
                IntPtr q = GetProcAddress(module, "nvapi_QueryInterface");
                if (q == IntPtr.Zero) return false;
                _query = (FnQuery)Marshal.GetDelegateForFunctionPointer(q, typeof(FnQuery));
                FnVoid init = Fn<FnVoid>(0x0150E828);
                _initialized = init != null && init() == NvapiOk;
            }
            catch (Exception ex) { CapLogShim(ex); }
            return _initialized;
        }

        private static T Fn<T>(uint id) where T : class
        {
            IntPtr p = _query(id);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        private static void CapLogShim(Exception ex) { Capture.CapLog.Report(ex); }

        public static bool Available { get { lock (Gate) return Load(); } }

        // Имя exe без пути: «game.exe». null — не годится (путь, пустое, не .exe, слишком длинное).
        public static string ValidExe(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            exe = exe.Trim();
            if (exe.Length > 128 || exe.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || exe.IndexOf('\\') >= 0 || exe.IndexOf('/') >= 0) return null;
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || exe.Length < 5) return null;
            return exe.ToLowerInvariant();
        }

        public static bool IsPrivilegeError(string error)
        {
            return error != null && error.IndexOf("(" + NvapiInvalidUserPrivilege.ToString(CultureInfo.InvariantCulture) + ")", StringComparison.Ordinal) >= 0;
        }

        private static IntPtr Unicode(string s, int bytes)
        {
            IntPtr p = Marshal.AllocHGlobal(bytes);
            ZeroMemory(p, bytes);
            byte[] b = Encoding.Unicode.GetBytes(s ?? "");
            Marshal.Copy(b, 0, p, Math.Min(b.Length, bytes - 2));
            return p;
        }

        private static void ZeroMemory(IntPtr p, int bytes)
        {
            byte[] zero = new byte[bytes];
            Marshal.Copy(zero, 0, p, bytes);
        }

        private static IntPtr NewSetting()
        {
            IntPtr s = Marshal.AllocHGlobal(SettingSize);
            ZeroMemory(s, SettingSize);
            Marshal.WriteInt32(s, 0, SettingSize | (1 << 16));
            return s;
        }

        private static string Err(string what, int rc)
        {
            return "NVAPI " + what + " (" + rc.ToString(CultureInfo.InvariantCulture) + ")";
        }

        private delegate string SessionWork(IntPtr session);

        private static string WithSession(SessionWork work)
        {
            lock (Gate)
            {
                if (!Load()) return Tr.S("драйвер NVIDIA (NVAPI) недоступен", "the NVIDIA driver (NVAPI) is not available");
                FnOutHandle create = Fn<FnOutHandle>(0x0694D52E);
                FnHandle destroy = Fn<FnHandle>(0xDAD9CFF8), load = Fn<FnHandle>(0x375DBD6B);
                if (create == null || destroy == null || load == null) return Tr.S("в этом драйвере нет профилей NVAPI", "this driver has no NVAPI profiles");
                IntPtr session;
                int rc = create(out session);
                if (rc != NvapiOk) return Err("CreateSession", rc);
                try
                {
                    rc = load(session);
                    if (rc != NvapiOk) return Err("LoadSettings", rc);
                    return work(session);
                }
                finally { destroy(session); }
            }
        }

        // Профиль: exe == null — общий (базовый); иначе профиль, к которому драйвер привязал этот exe. create —
        // создать свой профиль «WPC game.exe», если exe ни к какому не привязан.
        private static int FindProfile(IntPtr session, string exe, bool create, out IntPtr profile)
        {
            profile = IntPtr.Zero;
            if (exe == null)
            {
                FnHandleOut baseProfile = Fn<FnHandleOut>(0xDA8466A0);
                return baseProfile == null ? -1 : baseProfile(session, out profile);
            }
            FnFindApp find = Fn<FnFindApp>(0xEEE566B2);
            if (find == null) return -1;
            IntPtr name = Unicode(exe, NameChars * 2), app = Marshal.AllocHGlobal(AppSize);
            try
            {
                ZeroMemory(app, AppSize);
                Marshal.WriteInt32(app, 0, AppSize | (1 << 16));
                int rc = find(session, name, out profile, app);
                if (rc == NvapiOk || !create) return rc;

                FnFindProfile findProfile = Fn<FnFindProfile>(0x7E4A9A0B);
                FnCreateProfile createProfile = Fn<FnCreateProfile>(0xCC176068);
                FnCreateApp createApp = Fn<FnCreateApp>(0x4347A9DE);
                if (findProfile == null || createProfile == null || createApp == null) return -1;
                IntPtr profileName = Unicode(ProfilePrefix + exe, NameChars * 2), info = Marshal.AllocHGlobal(ProfileSize);
                try
                {
                    rc = findProfile(session, profileName, out profile);
                    if (rc != NvapiOk)
                    {
                        ZeroMemory(info, ProfileSize);
                        Marshal.WriteInt32(info, 0, ProfileSize | (1 << 16));
                        Marshal.Copy(Encoding.Unicode.GetBytes(ProfilePrefix + exe), 0, new IntPtr(info.ToInt64() + 4), Encoding.Unicode.GetByteCount(ProfilePrefix + exe));
                        rc = createProfile(session, info, out profile);
                        if (rc != NvapiOk) return rc;
                    }
                    ZeroMemory(app, AppSize);
                    Marshal.WriteInt32(app, 0, AppSize | (1 << 16));
                    byte[] exeBytes = Encoding.Unicode.GetBytes(exe);
                    Marshal.Copy(exeBytes, 0, new IntPtr(app.ToInt64() + 8), exeBytes.Length);             // appName
                    Marshal.Copy(exeBytes, 0, new IntPtr(app.ToInt64() + 8 + NameChars * 2), exeBytes.Length); // userFriendlyName
                    return createApp(session, profile, app);
                }
                finally { Marshal.FreeHGlobal(profileName); Marshal.FreeHGlobal(info); }
            }
            finally { Marshal.FreeHGlobal(name); Marshal.FreeHGlobal(app); }
        }

        private static string ProfileName(IntPtr session, IntPtr profile)
        {
            FnProfileInfo info = Fn<FnProfileInfo>(0x61CD6FD6);
            if (info == null) return null;
            IntPtr p = Marshal.AllocHGlobal(ProfileSize);
            try
            {
                ZeroMemory(p, ProfileSize);
                Marshal.WriteInt32(p, 0, ProfileSize | (1 << 16));
                if (info(session, profile, p) != NvapiOk) return null;
                return Marshal.PtrToStringUni(new IntPtr(p.ToInt64() + 4));
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        // Текущий предел: 0 — выключен (или не задан), -1 — не удалось прочитать (причина в error).
        public static int Get(string exe, out string error)
        {
            int result = -1;
            if (exe != null && ValidExe(exe) == null) { error = "exe"; return -1; }
            string exeName = exe == null ? null : ValidExe(exe);
            error = WithSession(delegate(IntPtr session)
            {
                IntPtr profile;
                int rc = FindProfile(session, exeName, false, out profile);
                if (rc != NvapiOk)
                {
                    if (exeName != null) { result = 0; return null; }   // exe ни к какому профилю не привязан — предела нет
                    return Err("GetBaseProfile", rc);
                }
                FnGetSetting get = Fn<FnGetSetting>(0x73BF8338);
                if (get == null) return Err("GetSetting", -1);
                IntPtr s = NewSetting();
                try
                {
                    rc = get(session, profile, FrlFpsId, s);
                    if (rc == NvapiSettingNotFound) { result = 0; return null; }
                    if (rc != NvapiOk) return Err("GetSetting", rc);
                    result = Marshal.ReadInt32(s, SettingCurrentOff);
                    return null;
                }
                finally { Marshal.FreeHGlobal(s); }
            });
            return error == null ? result : -1;
        }

        // fps 0 — убрать предел (удалить настройку из профиля). null — записано, иначе причина.
        public static string Set(string exe, int fps)
        {
            if (fps < 0 || fps > MaxFps) return "fps";
            string exeName = null;
            if (exe != null) { exeName = ValidExe(exe); if (exeName == null) return "exe"; }
            return WithSession(delegate(IntPtr session)
            {
                IntPtr profile;
                int rc = FindProfile(session, exeName, fps > 0, out profile);
                if (rc != NvapiOk)
                {
                    if (fps == 0 && exeName != null) return null;              // и так не задан
                    return Err(exeName == null ? "GetBaseProfile" : "CreateProfile", rc);
                }
                if (fps == 0)
                {
                    FnDeleteSetting del = Fn<FnDeleteSetting>(0xE4A26362);
                    if (del == null) return Err("DeleteProfileSetting", -1);
                    rc = del(session, profile, FrlFpsId);
                    if (rc != NvapiOk && rc != NvapiSettingNotFound) return Err("DeleteProfileSetting", rc);
                    // Свой профиль «WPC game.exe» после снятия предела пуст — удаляется целиком. Чужие не трогаются.
                    if (exeName != null && ProfileName(session, profile) == ProfilePrefix + exeName)
                    {
                        FnDeleteProfile deleteProfile = Fn<FnDeleteProfile>(0x17093206);
                        rc = deleteProfile == null ? -1 : deleteProfile(session, profile);
                        if (rc != NvapiOk) return Err("DeleteProfile", rc);
                    }
                }
                else
                {
                    FnSetSetting set = Fn<FnSetSetting>(0x577DD202);
                    if (set == null) return Err("SetSetting", -1);
                    IntPtr s = NewSetting();
                    try
                    {
                        Marshal.WriteInt32(s, SettingIdOff, unchecked((int)FrlFpsId));
                        Marshal.WriteInt32(s, SettingTypeOff, 0);                  // NVDRS_DWORD_TYPE
                        Marshal.WriteInt32(s, SettingCurrentOff, fps);
                        rc = set(session, profile, s);
                        if (rc != NvapiOk) return Err("SetSetting", rc);
                    }
                    finally { Marshal.FreeHGlobal(s); }
                }
                FnHandle save = Fn<FnHandle>(0xFCBC7E14);
                if (save == null) return Err("SaveSettings", -1);
                rc = save(session);
                return rc == NvapiOk ? null : Err("SaveSettings", rc);
            });
        }
    }
}
