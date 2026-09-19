// SysDeck — нативный слой страницы «Экраны»: подключение и отключение дисплеев средствами Windows.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Используется CCD API (Connecting and Configuring Displays, user32): QueryDisplayConfig отдаёт все пути
// «источник → приёмник», SetDisplayConfig применяет изменённый набор. Отключение дисплея — это снятие флага
// DISPLAYCONFIG_PATH_ACTIVE с его пути; кабель при этом остаётся в разъёме, меняется только конфигурация
// рабочего стола, и Windows сохраняет её в своей базе (SDC_SAVE_TO_DATABASE), поэтому раскладка переживает
// перезагрузку.
//
// Почему не модуль PowerShell DisplayConfig, на котором сделан скрипт tv-switch: модуль надо ставить из
// Галереи, он требует -ExecutionPolicy Bypass (иначе его .ps1xml блокируется), и каждый вызов — это запуск
// powershell.exe. Странице приложения ни одно из этих условий не подходит. Скрипт tv-switch остаётся в
// «Скриптах» для задач Планировщика, страница работает сама по себе.
//
// Тонкость режимов: при включении ранее отключённого дисплея индексы режимов пути ставятся в
// DISPLAYCONFIG_PATH_MODE_IDX_INVALID — тогда Windows сама подбирает режим и позицию. Передать чужие
// индексы нельзя: массив режимов при отключении переиндексируется.
using System;
using System.Runtime.InteropServices;

namespace SysDeck
{
    internal static class DispNative
    {
        // ---------- флаги ----------
        public const uint QDC_ALL_PATHS = 0x00000001;
        public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

        public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
        public const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;

        public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
        public const uint SDC_APPLY = 0x00000080;
        public const uint SDC_NO_OPTIMIZATION = 0x00000100;
        public const uint SDC_SAVE_TO_DATABASE = 0x00000200;
        public const uint SDC_ALLOW_CHANGES = 0x00000400;

        public const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
        public const uint DISPLAYCONFIG_MODE_INFO_TYPE_TARGET = 2;

        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

        public const int ERROR_SUCCESS = 0;
        public const int ERROR_INVALID_PARAMETER = 87;

        // ---------- структуры ----------
        [StructLayout(LayoutKind.Sequential)]
        public struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_2DREGION { public uint cx; public uint cy; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINTL { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECTL { public int left; public int top; public int right; public int bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
        {
            public ulong pixelRate;
            public DISPLAYCONFIG_RATIONAL hSyncFreq;
            public DISPLAYCONFIG_RATIONAL vSyncFreq;
            public DISPLAYCONFIG_2DREGION activeSize;
            public DISPLAYCONFIG_2DREGION totalSize;
            public uint videoStandard;      // вместе с битовым полем AdditionalSignalInfo
            public uint scanLineOrdering;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_TARGET_MODE { public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_SOURCE_MODE
        {
            public uint width;
            public uint height;
            public uint pixelFormat;
            public POINTL position;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
        {
            public POINTL PathSourceSize;
            public RECTL DesktopImageRegion;
            public RECTL DesktopImageClip;
        }

        // Объединение из заголовка: в одном и том же месте лежит режим приёмника, источника или
        // рабочего стола — что именно, говорит infoType.
        [StructLayout(LayoutKind.Explicit)]
        public struct DISPLAYCONFIG_MODE_INFO_UNION
        {
            [FieldOffset(0)] public DISPLAYCONFIG_TARGET_MODE targetMode;
            [FieldOffset(0)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
            [FieldOffset(0)] public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            public DISPLAYCONFIG_MODE_INFO_UNION mode;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }

        // ---------- вызовы ----------
        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        public static extern int SetDisplayConfig(uint numPathArrayElements, [In] DISPLAYCONFIG_PATH_INFO[] pathArray,
            uint numModeInfoArrayElements, [In] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, uint flags);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        // Имя монитора так, как его показывают параметры экрана, и путь устройства — устойчивый
        // идентификатор строки в списке (id приёмника меняется при переподключении, путь — нет).
        public static bool TargetName(LUID adapterId, uint targetId, out string friendly, out string devicePath, out uint outputTechnology)
        {
            friendly = null; devicePath = null; outputTechnology = 0;
            DISPLAYCONFIG_TARGET_DEVICE_NAME p = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            p.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            p.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_TARGET_DEVICE_NAME));
            p.header.adapterId = adapterId;
            p.header.id = targetId;
            if (DisplayConfigGetDeviceInfo(ref p) != ERROR_SUCCESS) return false;
            friendly = (p.monitorFriendlyDeviceName ?? "").Trim();
            devicePath = (p.monitorDevicePath ?? "").Trim();
            outputTechnology = p.outputTechnology;
            return true;
        }

        // Имя вида GDI (\\.\DISPLAY1) — по нему строку списка узнаёт пользователь и соотносят
        // параметры экрана Windows.
        public static string SourceName(LUID adapterId, uint sourceId)
        {
            DISPLAYCONFIG_SOURCE_DEVICE_NAME p = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            p.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            p.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME));
            p.header.adapterId = adapterId;
            p.header.id = sourceId;
            if (DisplayConfigGetDeviceInfo(ref p) != ERROR_SUCCESS) return null;
            return (p.viewGdiDeviceName ?? "").Trim();
        }

        // Тип разъёма словами. Значения — DISPLAYCONFIG_OUTPUT_TECHNOLOGY из wingdi.h. Нумерация
        // начинается с OTHER = -1, а HD15 (VGA) = 0; таблица, начатая с нуля, сдвигает весь список
        // на единицу и показывает HDMI как DVI — проверено живым прогоном 19.09.2026 на этой машине.
        public static string Connector(uint outputTechnology)
        {
            switch (outputTechnology)
            {
                case 0xFFFFFFFF: return Tr.S("Другой", "Other");
                case 0: return "VGA";
                case 1: return "S-Video";
                case 2: return "Composite";
                case 3: return "Component";
                case 4: return "DVI";
                case 5: return "HDMI";
                case 6: return "LVDS";
                case 8: return "D-Jpn";
                case 9: return "SDI";
                case 10: return "DisplayPort";
                case 11: return Tr.S("DisplayPort (встроенный)", "DisplayPort (embedded)");
                case 12: return "UDI";
                case 13: return Tr.S("UDI (встроенный)", "UDI (embedded)");
                case 14: return "SDTV";
                case 15: return "Miracast";
                case 16: return Tr.S("Проводной косвенный", "Indirect wired");
                case 0x80000000: return Tr.S("Встроенный", "Internal");
                default: return "?";
            }
        }

        public static bool SameLuid(LUID a, LUID b) { return a.LowPart == b.LowPart && a.HighPart == b.HighPart; }

        public static string LuidKey(LUID a)
        {
            return a.HighPart.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + ":"
                 + a.LowPart.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
