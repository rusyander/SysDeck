// Windows Process Cleaner — «Размеры папок»: какое окно Проводника впереди, какую папку оно показывает, где его строки.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WindowsProcessCleaner.FolderSize
{
    // ------------------------------------------------------------------ //
    //  Ровно столько COM-API UI Automation, сколько нужно, чтобы прочитать, где Проводник рисует строки и
    //  ячейки «Размер». Объявлено руками вместо UIAutomationClient: вызываются четыре метода. Неиспользуемые
    //  слоты — заглушки, потому что COM-интерфейс это vtable: контракт — ПОРЯДОК, и каждый предыдущий метод
    //  обязан занимать своё место, даже если его никто не зовёт.
    // ------------------------------------------------------------------ //
    internal static class Uia
    {
        public static readonly Guid ClsidCUIAutomation = new Guid("ff48dba4-60ef-4201-aa87-54103eef594e");

        public const int NamePropertyId = 30005;
        public const int BoundingRectanglePropertyId = 30001;
        public const int ClassNamePropertyId = 30012;
        public const int AutomationIdPropertyId = 30011;

        public const int TreeScopeElement = 1;
        public const int TreeScopeChildren = 2;
        public const int TreeScopeDescendants = 4;

        public const int AutomationElementModeNone = 0;

        public static IUIAutomation TryCreate()
        {
            try
            {
                Type type = Type.GetTypeFromCLSID(ClsidCUIAutomation, false);
                return type == null ? null : Activator.CreateInstance(type) as IUIAutomation;
            }
            catch (Exception ex)
            {
                FsLog.Report(ex);
                return null;
            }
        }

        // Закэшированный BoundingRectangle приходит VARIANT-ом с четырьмя double.
        public static Rectangle ToRectangle(object value)
        {
            double[] r = value as double[];
            if (r == null || r.Length != 4) return Rectangle.Empty;
            return new Rectangle((int)Math.Round(r[0]), (int)Math.Round(r[1]), (int)Math.Round(r[2]), (int)Math.Round(r[3]));
        }
    }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomation
    {
        void CompareElements_Stub();
        void CompareRuntimeIds_Stub();
        void GetRootElement_Stub();

        [return: MarshalAs(UnmanagedType.Interface)]
        IUIAutomationElement ElementFromHandle(IntPtr hwnd);

        void ElementFromPoint_Stub();
        void GetFocusedElement_Stub();
        void GetRootElementBuildCache_Stub();
        void ElementFromHandleBuildCache_Stub();
        void ElementFromPointBuildCache_Stub();
        void GetFocusedElementBuildCache_Stub();
        void CreateTreeWalker_Stub();
        void get_ControlViewWalker_Stub();
        void get_ContentViewWalker_Stub();
        void get_RawViewWalker_Stub();
        void get_RawViewCondition_Stub();
        void get_ControlViewCondition_Stub();
        void get_ContentViewCondition_Stub();

        [return: MarshalAs(UnmanagedType.Interface)]
        IUIAutomationCacheRequest CreateCacheRequest();

        [return: MarshalAs(UnmanagedType.Interface)]
        IUIAutomationCondition CreateTrueCondition();

        void CreateFalseCondition_Stub();

        [return: MarshalAs(UnmanagedType.Interface)]
        IUIAutomationCondition CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationElement
    {
        void SetFocus_Stub();
        void GetRuntimeId_Stub();

        [return: MarshalAs(UnmanagedType.Interface)]
        IUIAutomationElement FindFirst(int scope, IUIAutomationCondition condition);

        void FindAll_Stub();
        void FindFirstBuildCache_Stub();

        [return: MarshalAs(UnmanagedType.Interface)]
        IUIAutomationElementArray FindAllBuildCache(int scope, IUIAutomationCondition condition, IUIAutomationCacheRequest cacheRequest);

        void BuildUpdatedCache_Stub();

        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCurrentPropertyValue(int propertyId);

        void GetCurrentPropertyValueEx_Stub();

        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCachedPropertyValue(int propertyId);

        void GetCachedPropertyValueEx_Stub();
        void GetCurrentPatternAs_Stub();
        void GetCachedPatternAs_Stub();
        void GetCurrentPattern_Stub();
        void GetCachedPattern_Stub();
        void GetCachedParent_Stub();

        [return: MarshalAs(UnmanagedType.Interface)]
        IUIAutomationElementArray GetCachedChildren();
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationElementArray
    {
        int Length { [return: MarshalAs(UnmanagedType.I4)] get; }

        [return: MarshalAs(UnmanagedType.Interface)]
        IUIAutomationElement GetElement(int index);
    }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationCondition
    {
    }

    [ComImport, Guid("b32a92b5-bc25-4078-9c08-d7ee95c48e03"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationCacheRequest
    {
        void AddProperty(int propertyId);
        void AddPattern_Stub();
        void Clone_Stub();

        int TreeScope { get; set; }

        IUIAutomationCondition TreeFilter
        {
            [return: MarshalAs(UnmanagedType.Interface)] get;
            [param: MarshalAs(UnmanagedType.Interface)] set;
        }

        int AutomationElementMode { get; set; }
    }

    internal struct ExplorerState
    {
        public IntPtr Hwnd;
        public string Path;
        public Rectangle Bounds;
        public bool Visible;

        public ExplorerState(IntPtr hwnd, string path, Rectangle bounds, bool visible)
        {
            Hwnd = hwnd; Path = path; Bounds = bounds; Visible = visible;
        }

        public static readonly ExplorerState Hidden = new ExplorerState(IntPtr.Zero, null, Rectangle.Empty, false);

        public bool SameAs(ExplorerState other)
        {
            return Hwnd == other.Hwnd && string.Equals(Path, other.Path, StringComparison.Ordinal) && Bounds == other.Bounds && Visible == other.Visible;
        }

        public override string ToString()
        {
            return "hwnd=" + Hwnd + " visible=" + Visible + " bounds=" + Bounds + " path=" + (Path ?? "<none>");
        }
    }

    // ------------------------------------------------------------------ //
    //  Знает, какое окно Проводника впереди, какую папку оно показывает и где стоит на экране.
    //  Хуки WinEvent дают мгновенную реакцию; медленный таймер — страховка для того, что хуки пропускают
    //  (переключение вкладок, перезапуск explorer.exe, хук, молча снятый системой).
    // ------------------------------------------------------------------ //
    internal sealed class ExplorerTracker : IDisposable
    {
        private static readonly string[] ExplorerClasses = { "CabinetWClass", "ExploreWClass" };

        private readonly ShellPathResolver _resolver = new ShellPathResolver();
        private readonly System.Windows.Forms.Timer _poll = new System.Windows.Forms.Timer();
        private readonly Win32.WinEventProc _callback;         // поле: делегат обязан пережить хук
        private readonly uint _ownProcessId = (uint)Process.GetCurrentProcess().Id;

        private IntPtr _foregroundHook, _minimizeHook, _scopedHook;
        private uint _scopedProcessId;

        private IntPtr _tracked;
        private string _trackedTitle;
        private ExplorerState _state = ExplorerState.Hidden;
        private bool _disposed;

        public event Action<ExplorerState> Changed;

        public ExplorerState Current { get { return _state; } }

        // Следовать за окном Проводника, даже когда впереди другая программа, пока его видно на экране.
        // Выключено — Проводник, переставший быть активным окном, считается ушедшим.
        public bool KeepWhenUnfocused { get; set; }

        public ExplorerTracker()
        {
            _callback = OnWinEvent;
            _poll.Interval = 600;
            _poll.Tick += delegate { Evaluate(false); };
        }

        public void Start()
        {
            _foregroundHook = Hook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, 0);
            _minimizeHook = Hook(Win32.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_SYSTEM_MINIMIZEEND, 0);
            _poll.Start();
            Evaluate(false);
        }

        // Пересмотреть сейчас — после ручного обновления или когда панель показана снова.
        public void Refresh() { Evaluate(true); }

        private IntPtr Hook(uint min, uint max, uint processId)
        {
            return Win32.SetWinEventHook(min, max, IntPtr.Zero, _callback, processId, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (_disposed || idObject != Win32.OBJID_WINDOW) return;
            switch (eventType)
            {
                case Win32.EVENT_SYSTEM_FOREGROUND:
                case Win32.EVENT_SYSTEM_MINIMIZESTART:
                case Win32.EVENT_SYSTEM_MINIMIZEEND:
                    Evaluate(false);
                    break;
                // Привязаны к процессу отслеживаемого Проводника, поэтому здесь тихо.
                case Win32.EVENT_OBJECT_LOCATIONCHANGE:
                case Win32.EVENT_OBJECT_NAMECHANGE:
                case Win32.EVENT_OBJECT_DESTROY:
                    if (hwnd == _tracked) Evaluate(false);
                    break;
            }
        }

        private void Evaluate(bool force)
        {
            if (_disposed) return;
            try
            {
                ExplorerState next = Resolve(force);
                if (force || !next.SameAs(_state))
                {
                    _state = next;
                    FsLog.Trace("explorer " + next);
                    Action<ExplorerState> handler = Changed;
                    if (handler != null) handler(next);
                }
            }
            catch (Exception ex)
            {
                FsLog.Report(ex);
            }
        }

        private ExplorerState Resolve(bool force)
        {
            IntPtr foreground = Win32.GetForegroundWindow();
            bool explorerInFront = IsExplorerWindow(foreground);
            bool ours = !explorerInFront && BelongsToUs(foreground);

            if (explorerInFront)
            {
                Track(foreground);
            }
            else if (!ours && !KeepWhenUnfocused)
            {
                // Экран занят другой программой — панели не к чему пристроиться.
                ExplorerState hidden = _state;
                hidden.Visible = false;
                return hidden;
            }

            if (_tracked == IntPtr.Zero || !Win32.IsWindow(_tracked) || !Win32.IsWindowVisible(_tracked) || Win32.IsIconic(_tracked))
            {
                _tracked = IntPtr.Zero;
                return ExplorerState.Hidden;
            }

            string title = Win32.GetTitleOf(_tracked);
            Rectangle bounds = Win32.GetVisualBounds(_tracked);

            // Открыт — не то же самое, что на экране. Неактивный Проводник может быть закрыт тем, что впереди,
            // а наши поверхности обязаны быть поверх всех, чтобы вообще показываться над ним. Поэтому «всё ещё
            // видно» должно значить «его пиксели всё ещё на экране» — иначе панель и числа висели бы над
            // программой, на которую пользователь переключился.
            bool showing = explorerInFront || ours || Win32.IsAnyPartShowing(_tracked, bounds);

            // Спросить оболочку — дорогая часть: только когда сменился заголовок (переход или вкладка),
            // окно, или обновление попросили явно.
            string path = _state.Path;
            if (force || _state.Hwnd != _tracked || !string.Equals(title, _trackedTitle, StringComparison.Ordinal) || path == null)
            {
                path = _resolver.Resolve(_tracked, title) ?? path;
                _trackedTitle = title;
            }
            return new ExplorerState(_tracked, path, bounds, showing);
        }

        private void Track(IntPtr hwnd)
        {
            if (_tracked == hwnd) return;
            _tracked = hwnd;
            _trackedTitle = null;
            ScopeHookTo(hwnd);
        }

        // LOCATIONCHANGE — одно из самых шумных событий Windows: глобальный хук будил бы процесс на каждую
        // анимацию на экране. Поэтому он привязан к процессу Проводника.
        private void ScopeHookTo(IntPtr hwnd)
        {
            uint pid;
            Win32.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0 || pid == _scopedProcessId) return;
            if (_scopedHook != IntPtr.Zero) Win32.UnhookWinEvent(_scopedHook);
            _scopedProcessId = pid;
            _scopedHook = Hook(Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_NAMECHANGE, pid);
        }

        private static bool IsExplorerWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            string className = Win32.GetClassNameOf(hwnd);
            foreach (string c in ExplorerClasses)
                if (string.Equals(c, className, StringComparison.Ordinal)) return true;
            return false;
        }

        private bool BelongsToUs(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            uint pid;
            Win32.GetWindowThreadProcessId(hwnd, out pid);
            return pid == _ownProcessId;
        }

        public static void OpenInExplorer(string path)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "\"" + path + "\"");
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                FsLog.Report(ex);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _poll.Stop();
            _poll.Dispose();
            if (_foregroundHook != IntPtr.Zero) Win32.UnhookWinEvent(_foregroundHook);
            if (_minimizeHook != IntPtr.Zero) Win32.UnhookWinEvent(_minimizeHook);
            if (_scopedHook != IntPtr.Zero) Win32.UnhookWinEvent(_scopedHook);
            _resolver.Dispose();
        }
    }

    // ------------------------------------------------------------------ //
    //  Спрашивает оболочку, какую папку показывает окно Проводника.
    //  Позднее связывание намеренно: ни interop-сборки, ни COM-ссылки, и неожиданный ответ оболочки
    //  превращается в «папка неизвестна», а не в исключение. Вызывать из STA-потока.
    // ------------------------------------------------------------------ //
    internal sealed class ShellPathResolver : IDisposable
    {
        private object _shell;
        private bool _disposed;

        private object Shell
        {
            get
            {
                if (_shell == null && !_disposed)
                {
                    Type type = Type.GetTypeFromProgID("Shell.Application");
                    if (type != null) _shell = Activator.CreateInstance(type);
                }
                return _shell;
            }
        }

        // Windows 11 держит несколько вкладок за одним HWND, так что окно может соответствовать нескольким
        // папкам. Заголовок окна всегда следует за АКТИВНОЙ вкладкой — им ничья и разрешается.
        public string Resolve(IntPtr hwnd, string windowTitle)
        {
            List<string> candidates = new List<string>(4);
            object windows = null;
            try
            {
                object shell = Shell;
                if (shell == null) return null;
                windows = Invoke(shell, "Windows", BindingFlags.InvokeMethod);
                if (windows == null) return null;
                int count = Convert.ToInt32(Invoke(windows, "Count", BindingFlags.GetProperty) ?? 0);
                long target = hwnd.ToInt64();
                for (int i = 0; i < count; i++)
                {
                    object item = null;
                    try
                    {
                        item = Invoke(windows, "Item", BindingFlags.InvokeMethod, i);
                        if (item == null) continue;
                        if (Convert.ToInt64(Invoke(item, "HWND", BindingFlags.GetProperty) ?? 0L) != target) continue;
                        string path = ReadFolderPath(item);
                        if (path != null && !ContainsIgnoreCase(candidates, path)) candidates.Add(path);
                    }
                    catch (COMException) { }                // окно, закрывшееся посреди перечисления, — норма
                    catch (TargetInvocationException) { }
                    finally { Release(item); }
                }
            }
            catch (COMException ex)
            {
                FsLog.Report(ex);
                // Оболочка может оборвать связь (перезапуск explorer.exe) — в следующий раз соберём заново.
                Release(_shell);
                _shell = null;
            }
            catch (Exception ex)
            {
                FsLog.Report(ex);
                Release(_shell);
                _shell = null;
            }
            finally
            {
                Release(windows);
            }
            return Pick(candidates, windowTitle);
        }

        private static bool ContainsIgnoreCase(List<string> list, string value)
        {
            foreach (string s in list) if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ReadFolderPath(object shellWindow)
        {
            object document = null, folder = null, self = null;
            try
            {
                document = Invoke(shellWindow, "Document", BindingFlags.GetProperty);
                if (document == null) return null;
                folder = Invoke(document, "Folder", BindingFlags.GetProperty);
                if (folder == null) return null;
                self = Invoke(folder, "Self", BindingFlags.GetProperty);
                if (self == null) return null;
                string path = Invoke(self, "Path", BindingFlags.GetProperty) as string;
                return IsRealDirectory(path) ? path : null;
            }
            catch (COMException) { return null; }            // окна в духе Internet Explorer и виртуальные папки
            catch (MissingMemberException) { return null; }
            catch (TargetInvocationException) { return null; }
            finally
            {
                Release(self);
                Release(folder);
                Release(document);
            }
        }

        // «Этот компьютер», Панель управления и подобные отдают CLSID, а не папку, которую можно измерить.
        internal static bool IsRealDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Trim().Length == 0 || path.StartsWith("::", StringComparison.Ordinal)) return false;
            bool rooted = (path.Length >= 2 && path[1] == ':') || path.StartsWith(@"\\", StringComparison.Ordinal);
            if (!rooted) return false;
            try { return Directory.Exists(path); }
            catch { return false; }
        }

        internal static string Pick(List<string> candidates, string windowTitle)
        {
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];
            string title = (windowTitle ?? "").Trim();
            foreach (string path in candidates)
            {
                string leaf = LeafName(path);
                if (leaf.Length > 0 && string.Equals(title, leaf, StringComparison.OrdinalIgnoreCase)) return path;
            }
            foreach (string path in candidates)
            {
                string leaf = LeafName(path);
                if (leaf.Length > 0 && title.IndexOf(leaf, StringComparison.OrdinalIgnoreCase) >= 0) return path;
            }
            return candidates[0];
        }

        private static string LeafName(string path)
        {
            string trimmed = path.TrimEnd('\\');
            int slash = trimmed.LastIndexOf('\\');
            return slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
        }

        private static object Invoke(object target, string member, BindingFlags flags, params object[] args)
        {
            return target.GetType().InvokeMember(member, flags, null, target, args.Length == 0 ? null : args);
        }

        private static void Release(object comObject)
        {
            if (comObject == null || !Marshal.IsComObject(comObject)) return;
            try { Marshal.ReleaseComObject(comObject); }
            catch { }                                     // уже освобождён
        }

        public void Dispose()
        {
            _disposed = true;
            Release(_shell);
            _shell = null;
        }
    }

    internal sealed class ExplorerRow
    {
        public readonly string Name;
        public readonly Rectangle Bounds;
        public readonly Rectangle SizeCell;

        public ExplorerRow(string name, Rectangle bounds, Rectangle sizeCell)
        {
            Name = name; Bounds = bounds; SizeCell = sizeCell;
        }
    }

    internal sealed class ExplorerListLayout
    {
        public readonly Rectangle ItemsView;
        public readonly Rectangle SizeColumn;
        public readonly IList<ExplorerRow> Rows;

        public ExplorerListLayout(Rectangle itemsView, Rectangle sizeColumn, IList<ExplorerRow> rows)
        {
            ItemsView = itemsView; SizeColumn = sizeColumn; Rows = rows;
        }
    }

    // ------------------------------------------------------------------ //
    //  Геометрия списка файлов Проводника: где столбец «Размер» и где ячейка «Размер» каждой видимой строки.
    //  Всё приходит одним закэшированным вызовом UI Automation на обновление. Список виртуализован: есть
    //  только строки на экране — ровно те, что стоит рисовать. Столбец узнаётся по AutomationId
    //  ("System.Size"), никогда по подписи: этот ключ одинаков на русской, английской и китайской Windows.
    // ------------------------------------------------------------------ //
    internal sealed class ExplorerListReader : IDisposable
    {
        private const string ItemsViewClass = "UIItemsView";
        private const string RowClass = "UIItem";
        private const string HeaderClass = "UIViewHeader";
        private const string SizePropertyKey = "System.Size";

        private IUIAutomation _automation;
        private IUIAutomationCacheRequest _cache;
        private IUIAutomationCondition _trueCondition;
        private IUIAutomationCondition _itemsViewCondition;

        private IUIAutomationElement _itemsView;
        private IntPtr _itemsViewOwner;
        private bool _unavailable;
        private string _reportedFailure;

        // Вызывать с фонового (MTA) потока, никогда с потока интерфейса: вызовы UIA пересекают процессы.
        public ExplorerListLayout Read(IntPtr explorerHwnd)
        {
            if (_unavailable || explorerHwnd == IntPtr.Zero) return null;
            try
            {
                if (!EnsureAutomation()) return null;
                IUIAutomationElement view = ResolveItemsView(explorerHwnd);
                if (view == null) return null;

                IUIAutomationElementArray children = view.FindAllBuildCache(Uia.TreeScopeChildren, _trueCondition, _cache);
                if (children == null || children.Length == 0)
                {
                    DropView();
                    return null;
                }

                List<ExplorerRow> rows = new List<ExplorerRow>(children.Length);
                Rectangle sizeColumn = Rectangle.Empty;
                // Сам список пришёл из FindFirst без кэша, поэтому отвечает только на живые запросы.
                Rectangle itemsViewBounds = Uia.ToRectangle(view.GetCurrentPropertyValue(Uia.BoundingRectanglePropertyId));

                for (int i = 0; i < children.Length; i++)
                {
                    IUIAutomationElement child = children.GetElement(i);
                    string className = child.GetCachedPropertyValue(Uia.ClassNamePropertyId) as string ?? "";
                    if (className == HeaderClass)
                    {
                        sizeColumn = FindSizeCell(child);
                    }
                    else if (className == RowClass)
                    {
                        Rectangle cell = FindSizeCell(child);
                        if (cell.IsEmpty) continue;
                        string name = child.GetCachedPropertyValue(Uia.NamePropertyId) as string ?? "";
                        if (name.Length == 0) continue;
                        rows.Add(new ExplorerRow(name, Uia.ToRectangle(child.GetCachedPropertyValue(Uia.BoundingRectanglePropertyId)), cell));
                    }
                }
                // Нет столбца «Размер» — пользователь не в режиме «Таблица», и числу негде стоять.
                if (sizeColumn.IsEmpty || rows.Count == 0) return null;
                return new ExplorerListLayout(itemsViewBounds, sizeColumn, rows);
            }
            catch (COMException)
            {
                // Проводник перешёл или закрылся, пока мы его читали: отпустить и повторить позже.
                DropView();
                return null;
            }
            catch (InvalidCastException ex)
            {
                // Форма, которую мы не понимаем, — выключиться, а не бороться с ней каждые 150 мс.
                FsLog.Report(ex);
                _unavailable = true;
                return null;
            }
            catch (Exception ex)
            {
                ReportOnce(ex);
                DropView();
                return null;
            }
        }

        // Выполняется несколько раз в секунду: повторяющийся сбой не должен растить журнал без предела.
        private void ReportOnce(Exception ex)
        {
            string key = ex.GetType().FullName + "|" + ex.Message;
            if (key == _reportedFailure) return;
            _reportedFailure = key;
            FsLog.Report(ex);
        }

        private static Rectangle FindSizeCell(IUIAutomationElement parent)
        {
            IUIAutomationElementArray cells = parent.GetCachedChildren();
            if (cells == null) return Rectangle.Empty;
            for (int i = 0; i < cells.Length; i++)
            {
                IUIAutomationElement cell = cells.GetElement(i);
                if (cell.GetCachedPropertyValue(Uia.AutomationIdPropertyId) as string == SizePropertyKey)
                    return Uia.ToRectangle(cell.GetCachedPropertyValue(Uia.BoundingRectanglePropertyId));
            }
            return Rectangle.Empty;
        }

        private bool EnsureAutomation()
        {
            if (_automation != null) return true;
            _automation = Uia.TryCreate();
            if (_automation == null)
            {
                _unavailable = true;
                return false;
            }
            _trueCondition = _automation.CreateTrueCondition();
            _itemsViewCondition = _automation.CreatePropertyCondition(Uia.ClassNamePropertyId, ItemsViewClass);
            // Один проход через границу процессов на обновление: свойства строки и её ячейки приходят вместе.
            _cache = _automation.CreateCacheRequest();
            _cache.AddProperty(Uia.NamePropertyId);
            _cache.AddProperty(Uia.ClassNamePropertyId);
            _cache.AddProperty(Uia.AutomationIdPropertyId);
            _cache.AddProperty(Uia.BoundingRectanglePropertyId);
            _cache.TreeScope = Uia.TreeScopeElement | Uia.TreeScopeChildren;
            _cache.AutomationElementMode = Uia.AutomationElementModeNone;
            return true;
        }

        private IUIAutomationElement ResolveItemsView(IntPtr explorerHwnd)
        {
            if (_itemsView != null && _itemsViewOwner == explorerHwnd) return _itemsView;
            DropView();
            IUIAutomationElement root = _automation.ElementFromHandle(explorerHwnd);
            _itemsView = root.FindFirst(Uia.TreeScopeDescendants, _itemsViewCondition);
            _itemsViewOwner = _itemsView == null ? IntPtr.Zero : explorerHwnd;
            return _itemsView;
        }

        private void DropView()
        {
            if (_itemsView != null) Release(_itemsView);
            _itemsView = null;
            _itemsViewOwner = IntPtr.Zero;
        }

        public void Dispose()
        {
            DropView();
            Release(_cache); _cache = null;
            Release(_trueCondition); _trueCondition = null;
            Release(_itemsViewCondition); _itemsViewCondition = null;
            Release(_automation); _automation = null;
        }

        private static void Release(object instance)
        {
            if (instance == null || !Marshal.IsComObject(instance)) return;
            try { Marshal.ReleaseComObject(instance); }
            catch { }
        }
    }
}
