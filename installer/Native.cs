using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WpcSetup
{
    internal static class Native
    {
        // Удаление того, что держат открытым: файл уйдёт при следующей загрузке Windows.
        // Работает через HKLM\...\PendingFileRenameOperations, то есть только с правами
        // администратора — для установки «только для меня» это лишь запасной путь.
        public const int MoveFileDelayUntilReboot = 0x4;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveFileEx(string existingName, string newName, int flags);

        // Приложение работает от администратора, а установщик — от обычного пользователя;
        // Process.MainModule в такой паре отвечает «отказано в доступе», а этот запрос
        // разрешён и через границу уровней целостности.
        public const int ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        public static string ImagePath(int pid)
        {
            if (pid <= 0) return null;
            IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (!QueryFullProcessImageName(h, 0, sb, ref size)) return null;
                return sb.ToString(0, size);
            }
            catch { return null; }
            finally { CloseHandle(h); }
        }
    }

    // Ярлыки делаются через COM-интерфейсы оболочки, объявленные руками: csc.exe из состава
    // Windows не умеет добавлять COM-ссылки (нет ни tlbimp, ни NuGet), а WSH-объект
    // (WScript.Shell через late binding) требует включённого Windows Script Host, который
    // в организациях часто выключают политикой. Порядок методов в интерфейсах критичен —
    // это vtable, а не имена.
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLinkCoClass
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, int flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relative, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPersistFileW
    {
        void GetClassID(out Guid classId);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, int mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    internal static class Shortcut
    {
        // Один и тот же путь ярлыка при повторной установке перезаписывается, а не
        // дублируется — поэтому имя файла фиксированное, без версии и без счётчика.
        public static void Create(string lnkPath, string target, string workDir, string description)
        {
            string dir = System.IO.Path.GetDirectoryName(lnkPath);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

            object com = null;
            try
            {
                com = new ShellLinkCoClass();
                IShellLinkW link = (IShellLinkW)com;
                link.SetPath(target);
                link.SetWorkingDirectory(workDir);
                link.SetDescription(description);
                link.SetIconLocation(target, 0);
                link.SetShowCmd(1); // SW_SHOWNORMAL
                ((IPersistFileW)com).Save(lnkPath, true);
            }
            finally
            {
                if (com != null) Marshal.FinalReleaseComObject(com);
            }
        }
    }
}
