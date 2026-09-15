// SysDeck — «Захват»: фоновый процесс (ключ --capture), горячие клавиши, команды, сценарии снимков.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck.Capture
{
    // ------------------------------------------------------------------ //
    //  Ключи exe. Проверяются в Main раньше мьютекса главного окна: агент — отдельный процесс со своим мьютексом.
    // ------------------------------------------------------------------ //
    internal static class CapMode
    {
        public const string Switch = "--capture";              // фоновый процесс захвата
        public const string ExitSwitch = "--capture-exit";     // остановить работающий
        public const string ShotSwitch = "--capture-shot";     // region|screen|window — попросить работающий агент о снимке; hud — показать / скрыть показатели
        public const string WaitSwitch = "--capture-wait";     // вместе с --capture: дождаться выхода прежнего агента (перезапуск на новой сборке)

        public static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length == 0) return false;
            int at;
            if ((at = IndexOf(args, ExitSwitch)) >= 0)
            {
                exitCode = CapIpc.Signal("Shutdown") ? 0 : 1;
                return true;
            }
            if ((at = IndexOf(args, ShotSwitch)) >= 0)
            {
                string kind = at + 1 < args.Length ? args[at + 1] : "region";
                string command = ShotCommand(kind);
                // Оверлей — отдельный процесс: работает и без агента «Захвата».
                if (command == "Hud") { exitCode = HudLauncher.Toggle() == null ? 0 : 1; return true; }
                exitCode = command != null && CapIpc.Signal(command) ? 0 : command == null ? 2 : 1;
                return true;
            }
            if (IndexOf(args, Switch) >= 0)
            {
                exitCode = RunAgent(IndexOf(args, WaitSwitch) >= 0);
                return true;
            }
            return false;
        }

        internal static string ShotCommand(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "region": return "ShotRegion";
                case "screen": return "ShotScreen";
                case "window": return "ShotWindow";
                case "hud": return "Hud";
                default: return null;
            }
        }

        private static int IndexOf(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static int RunAgent(bool waitPrevious)
        {
            try { Tr.En = new Engine().Config.Language == "en"; }
            catch (Exception ex) { CapLog.Report(ex); }

            bool first;
            Mutex instance = CapIpc.CreateInstanceMutex(out first);
            if (!first && instance != null && waitPrevious)
            {
                // Прежний агент закрывает запись файлов и отпускает клавиши — обычно доли секунды.
                try { first = instance.WaitOne(15000); }
                catch (AbandonedMutexException) { first = true; }
            }
            if (!first)
            {
                if (instance != null) instance.Dispose();
                return 0;
            }

            List<EventWaitHandle> events = new List<EventWaitHandle>();
            List<RegisteredWaitHandle> waits = new List<RegisteredWaitHandle>();
            AgentApp app = null;
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { CapLog.Report(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { CapLog.Report(e.ExceptionObject as Exception); };
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // Выделение области, уведомления и снимки — в физических пикселях на любом мониторе.
                CapNative.UsePerMonitorDpiOnThisThread();

                app = new AgentApp();
                AgentApp target = app;
                foreach (string command in CapIpc.Commands)
                {
                    EventWaitHandle h = CapIpc.CreateEvent(command);
                    events.Add(h);
                    string cmd = command;
                    waits.Add(ThreadPool.RegisterWaitForSingleObject(h, delegate { target.Post(delegate { target.Command(cmd); }); },
                                                                     null, Timeout.Infinite, false));
                }
                app.Start();
                Application.Run();
            }
            catch (Exception ex) { CapLog.Report(ex); return 1; }
            finally
            {
                foreach (RegisteredWaitHandle w in waits) w.Unregister(null);
                if (app != null) app.Dispose();
                CapStatus.Clear();
                foreach (EventWaitHandle h in events) h.Dispose();
                try { instance.ReleaseMutex(); } catch { }
                instance.Dispose();
            }
            return 0;
        }
    }

    // ------------------------------------------------------------------ //
    //  Горячие клавиши: окно только для сообщений, WM_HOTKEY приходит в поток интерфейса агента
    // ------------------------------------------------------------------ //
    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
        private readonly List<int> _ids = new List<int>();

        public event Action<CapAction> Pressed;

        public HotkeyWindow()
        {
            CreateParams cp = new CreateParams();
            cp.Parent = HWND_MESSAGE;
            CreateHandle(cp);
        }

        // Код ошибки на каждое действие с назначенным сочетанием: 0 — зарегистрировано.
        public Dictionary<CapAction, int> RegisterAll(IDictionary<CapAction, HotkeySpec> keys)
        {
            UnregisterAll();
            Dictionary<CapAction, int> result = new Dictionary<CapAction, int>();
            foreach (KeyValuePair<CapAction, HotkeySpec> kv in keys)
            {
                if (kv.Value.IsEmpty) continue;
                int id = (int)kv.Key + 1;
                // MOD_NOREPEAT: удержание клавиши не запускает снимок за снимком.
                if (CapNative.RegisterHotKey(Handle, id, kv.Value.Mods | 0x4000, kv.Value.Vk))
                {
                    _ids.Add(id);
                    result[kv.Key] = 0;
                }
                else result[kv.Key] = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            }
            return result;
        }

        // Повторить только отказавшие (занятые другой программой) сочетания; true — что-то изменилось.
        public bool RetryFailed(IDictionary<CapAction, HotkeySpec> keys, Dictionary<CapAction, int> errors)
        {
            bool changed = false;
            foreach (CapAction a in new List<CapAction>(errors.Keys))
            {
                HotkeySpec spec;
                if (errors[a] <= 0 || !keys.TryGetValue(a, out spec) || spec.IsEmpty) continue;
                int id = (int)a + 1;
                if (!CapNative.RegisterHotKey(Handle, id, spec.Mods | 0x4000, spec.Vk)) continue;
                _ids.Add(id);
                errors[a] = 0;
                changed = true;
            }
            return changed;
        }

        public void UnregisterAll()
        {
            foreach (int id in _ids) CapNative.UnregisterHotKey(Handle, id);
            _ids.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == CapNative.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                Action<CapAction> pressed = Pressed;
                if (pressed != null && id >= 1 && id <= CapActions.All.Length)
                {
                    CapAction a = (CapAction)(id - 1);
                    CapLog.Swallow(delegate { pressed(a); });
                }
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            UnregisterAll();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    // Сочетание, занятое другой программой (NVIDIA App держит Alt+R, GameCenter — F3), RegisterHotKey не получит: Windows
    // отдаёт его тому, кто занял первым. Низкоуровневый хук клавиатуры видит нажатие раньше этой раздачи и забирает его.
    // Свой поток с очередью сообщений: Windows молча снимает хук, чей обработчик не ответил вовремя, а поток агента бывает
    // занят снимком. Win-сочетания не забираются — хук сломал бы меню «Пуск».
    internal sealed class KeyGrabber : IDisposable
    {
        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private const int WH_KEYBOARD_LL = 13;
        private const uint WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105, WM_QUIT = 0x12;
        private const uint KEYEVENTF_KEYUP = 2;
        // Не назначенная клавиша: после забранного Alt+R отпущенный Alt не выглядит одиночным нажатием и не открывает меню окна.
        private const byte VkMask = 0xE8;

        private readonly Action<CapAction> _pressed;
        private readonly HookProc _proc;   // ссылка держит делегат живым, пока хук стоит
        private volatile Dictionary<CapAction, HotkeySpec> _keys = new Dictionary<CapAction, HotkeySpec>();
        private Thread _thread;
        private uint _threadId;
        private IntPtr _hook;
        private uint _downVk;              // только в потоке хука

        public KeyGrabber(Action<CapAction> pressed)
        {
            _pressed = pressed;
            _proc = OnKey;
        }

        public bool Active { get { return _thread != null; } }

        // Из потока агента. false — хук не поставился, сочетания остаются «занято».
        public bool Set(Dictionary<CapAction, HotkeySpec> keys)
        {
            _keys = new Dictionary<CapAction, HotkeySpec>(keys);
            if (keys.Count == 0) { Stop(); return true; }
            if (_thread != null) return true;
            ManualResetEvent ready = new ManualResetEvent(false);
            bool installed = false;
            Thread t = new Thread(delegate()
            {
                _threadId = GetCurrentThreadId();
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                installed = _hook != IntPtr.Zero;
                if (!installed) CapLog.Write("keyboard hook failed, code " + Marshal.GetLastWin32Error());
                ready.Set();
                if (!installed) return;
                MSG m;
                while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            });
            t.Name = "wpc-capture-keygrab";
            t.IsBackground = true;
            t.Start();
            // Событие не закрывается: опоздавший поток ещё может его установить.
            ready.WaitOne(5000);
            if (!installed) { t.Join(1000); return false; }
            _thread = t;
            return true;
        }

        private void Stop()
        {
            if (_thread == null) return;
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(2000);
            _thread = null;
        }

        private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                uint msg = (uint)wParam.ToInt64();
                if (k.vkCode != VkMask)
                {
                    if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                    {
                        if (k.vkCode == _downVk) { _downVk = 0; return new IntPtr(1); }
                    }
                    else if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        // Автоповтор удерживаемой клавиши — без повторного срабатывания (как MOD_NOREPEAT).
                        if (k.vkCode == _downVk) return new IntPtr(1);
                        uint mods = CurrentMods();
                        CapAction action;
                        if (KeyGrab.Match(_keys, k.vkCode, mods, out action))
                        {
                            _downVk = k.vkCode;
                            if ((mods & HotkeySpec.MOD_ALT) != 0)
                            {
                                keybd_event(VkMask, 0, 0, UIntPtr.Zero);
                                keybd_event(VkMask, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                            }
                            try { _pressed(action); }
                            catch (Exception ex) { CapLog.Report(ex); }
                            return new IntPtr(1);
                        }
                    }
                }
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        private static uint CurrentMods()
        {
            uint mods = 0;
            if (CapNative.GetAsyncKeyState(0x11) < 0) mods |= HotkeySpec.MOD_CONTROL;
            if (CapNative.GetAsyncKeyState(0x12) < 0) mods |= HotkeySpec.MOD_ALT;
            if (CapNative.GetAsyncKeyState(0x10) < 0) mods |= HotkeySpec.MOD_SHIFT;
            if (CapNative.GetAsyncKeyState(0x5B) < 0 || CapNative.GetAsyncKeyState(0x5C) < 0) mods |= HotkeySpec.MOD_WIN;
            return mods;
        }

        public void Dispose() { Stop(); }
    }

    internal static class KeyGrab
    {
        // Забирать можно сочетание с Ctrl/Alt/Shift или одиночную клавишу; с Win — нет.
        public static bool Eligible(HotkeySpec spec)
        {
            return !spec.IsEmpty && (spec.Mods & HotkeySpec.MOD_WIN) == 0;
        }

        // Точное совпадение: Alt+R не срабатывает на Ctrl+Alt+R и наоборот.
        public static bool Match(IDictionary<CapAction, HotkeySpec> keys, uint vk, uint mods, out CapAction action)
        {
            foreach (KeyValuePair<CapAction, HotkeySpec> kv in keys)
                if (kv.Value.Vk == vk && kv.Value.Mods == mods) { action = kv.Key; return true; }
            action = CapAction.ShotRegion;
            return false;
        }

        // Какие отказавшие из-за занятости сочетания забрать хуком.
        public static Dictionary<CapAction, HotkeySpec> Busy(IDictionary<CapAction, HotkeySpec> keys, IDictionary<CapAction, int> errors, bool take)
        {
            Dictionary<CapAction, HotkeySpec> grab = new Dictionary<CapAction, HotkeySpec>();
            if (!take) return grab;
            foreach (KeyValuePair<CapAction, int> kv in errors)
            {
                HotkeySpec spec;
                if (kv.Value == HotkeyOwners.ErrorHotkeyAlreadyRegistered && keys.TryGetValue(kv.Key, out spec) && Eligible(spec)) grab[kv.Key] = spec;
            }
            return grab;
        }
    }

    // Смена сборки exe под работающим агентом.
    internal static class AgentBuild
    {
        // Время записи exe; MinValue — файл не читается (переименован и ещё не заменён, удалён).
        public static DateTime Stamp(string exe)
        {
            try { return File.Exists(exe) ? File.GetLastWriteTimeUtc(exe) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        // Компилятор пишет exe несколько секунд: новый файл должен пролежать нетронутым 10 с.
        public static bool ShouldRestart(DateTime started, DateTime now, DateTime utcNow, bool busy)
        {
            if (busy || started == DateTime.MinValue || now == DateTime.MinValue || now == started) return false;
            return utcNow - now >= TimeSpan.FromSeconds(10);
        }
    }

    // Что уже умеет модуль: сочетания ещё не сделанных действий не занимаются у других программ.
    internal static class CapFeatures
    {
        public static readonly bool Video = true;
        public static readonly bool Gallery = true;
        public static readonly bool Editor = true;

        public static bool Available(CapAction a)
        {
            switch (a)
            {
                case CapAction.RecRegion: case CapAction.RecScreen: case CapAction.RecPause: return Video;
                case CapAction.Gallery: return Gallery;
                default: return true;
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  Корень агента
    // ------------------------------------------------------------------ //
    internal sealed class AgentApp : IDisposable
    {
        private readonly Control _invoker;
        private readonly HotkeyWindow _hotkeys;
        private readonly ToastHost _toasts;
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private CapSettings _settings;
        private RegionOverlay _overlay;
        private System.Windows.Forms.Timer _delay;
        private readonly System.Windows.Forms.Timer _retry = new System.Windows.Forms.Timer();
        private System.Windows.Forms.Timer _keysBack;
        private Dictionary<CapAction, HotkeySpec> _keys = new Dictionary<CapAction, HotkeySpec>();
        private Dictionary<CapAction, int> _keyErrors = new Dictionary<CapAction, int>();
        private readonly List<EditorForm> _editors = new List<EditorForm>();
        private RecordSession _record;
        private GalleryForm _gallery;
        private readonly System.Windows.Forms.Timer _buildWatch = new System.Windows.Forms.Timer();
        private readonly KeyGrabber _grabber;
        private readonly DateTime _buildStamp = AgentBuild.Stamp(CapPaths.ExecutablePath);

        public AgentApp()
        {
            _invoker = new Control();
            _invoker.CreateControl();
            IntPtr force = _invoker.Handle;
            _settings = CapSettings.Load();
            _hotkeys = new HotkeyWindow();
            _hotkeys.Pressed += OnHotkey;
            _grabber = new KeyGrabber(delegate(CapAction a) { Post(delegate { Run(a); }); });
            _toasts = new ToastHost();
            _toasts.EditRequested += EditFile;
            _toasts.GalleryRequested += ShowGallery;
            // Сочетание, занятое другой программой (GameCenter до удаления), подхватывается, как только освободится.
            _retry.Interval = 15000;
            _retry.Tick += delegate { RetryBusyKeys(); };
            _buildWatch.Interval = 20000;
            _buildWatch.Tick += delegate { RestartIfRebuilt(); };
        }

        public CapSettings Settings { get { return _settings; } }

        public void Start()
        {
            ApplySettings();
            // Проба кодировщиков (до 2,5 с на новый размер) запоминается между запусками.
            VideoEncoders.CacheFile = CapPaths.EncoderCacheFile;
            RecoverInterruptedRecording();
            // Программа запускается с Windows свёрнутой в трей; галерея — единственное окно, которое по просьбе
            // пользователя должно открываться развёрнутым.
            if (_settings.GalleryOnStart) ShowGallery(null);
            // Оверлей — свой процесс (ему нужны права, агенту нельзя): агент лишь поднимает его вместе с собой.
            HudLauncher.StartIfWanted();
            _buildWatch.Start();
        }

        // exe пересобрали или обновили установщиком, а агент работает со старым кодом: держит прежние сочетания и
        // не знает новых команд. Перезапуск — только когда ничего не прервётся: нет записи, выделения, редактора, галереи.
        private void RestartIfRebuilt()
        {
            bool busy = _record != null || _overlay != null || _delay != null && _delay.Enabled || _editors.Count > 0 ||
                        _gallery != null && !_gallery.IsDisposed;
            string exe = CapPaths.ExecutablePath;
            if (!AgentBuild.ShouldRestart(_buildStamp, AgentBuild.Stamp(exe), DateTime.UtcNow, busy)) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, CapMode.Switch + " " + CapMode.WaitSwitch);
                psi.UseShellExecute = false;
                Process p = Process.Start(psi);
                if (p != null) p.Dispose();
            }
            catch (Exception ex)
            {
                // Новый exe не запускается (антивирус, файл ещё пишется) — старый агент продолжает работать.
                CapLog.Report(ex);
                _buildWatch.Stop();
                return;
            }
            _buildWatch.Stop();
            CapLog.Write("new build of the exe, agent restarts");
            Application.ExitThread();
        }

        // Метка записи пережила прошлый процесс — запись оборвалась (сбой, выключение). fMP4 чинится перемуксом.
        private void RecoverInterruptedRecording()
        {
            string pending = RecordMarker.Pending();
            if (pending == null) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string problem = null, fixedPath = null;
                try { fixedPath = RecordMarker.Recover(pending, out problem); }
                catch (Exception ex) { CapLog.Report(ex); problem = ex.Message; }
                RecordMarker.Clear();
                if (fixedPath == null && problem == null) return;
                Post(delegate
                {
                    MonitorInfo mon = ScreenGrab.MonitorAt(ScreenGrab.Monitors(), CapNative.CursorPosition());
                    if (fixedPath != null)
                    {
                        ToastInfo t = ToastInfo.VideoSaved(null, fixedPath, TimeSpan.FromTicks(Math.Max(0, FragmentedMp4.Duration(fixedPath))),
                                                           SafeLength(fixedPath), Tr.S("восстановлено после обрыва записи", "recovered after an interrupted recording") +
                                                           (problem != null ? " · " + problem : ""));
                        _toasts.Show(t, mon);
                    }
                    else
                        _toasts.Show(ToastInfo.Error(Tr.S("Прерванная запись", "Interrupted recording"), Path.GetFileName(pending) + ": " + problem), mon);
                });
            });
        }

        private static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        // Из любого потока: действие выполнится в потоке интерфейса агента.
        public void Post(Action action)
        {
            try { _invoker.BeginInvoke((MethodInvoker)delegate { CapLog.Swallow(action); }); }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        private void ApplySettings()
        {
            Dictionary<CapAction, HotkeySpec> keys = new Dictionary<CapAction, HotkeySpec>();
            foreach (CapAction a in CapActions.All)
                if (CapFeatures.Available(a)) keys[a] = _settings.Hotkey(a);
            if (_keysBack != null) _keysBack.Stop();
            _keys = keys;
            _keyErrors = _hotkeys.RegisterAll(keys);
            GrabBusyKeys();
            _toasts.Apply(_settings);
            CapStatus.Write(Elevation.IsElevated, _keyErrors, _startedUtc);
            UpdateRetry();
        }

        // Пока в главном окне вводят новое сочетание, агент не должен перехватывать нажатия. Если окно не вернёт
        // клавиши (закрыли, упало), через минуту они вернутся сами.
        private void HotkeysOff()
        {
            _hotkeys.UnregisterAll();
            _grabber.Set(new Dictionary<CapAction, HotkeySpec>());
            _retry.Stop();
            if (_keysBack == null)
            {
                _keysBack = new System.Windows.Forms.Timer();
                _keysBack.Interval = 60000;
                _keysBack.Tick += delegate { _keysBack.Stop(); ApplySettings(); };
            }
            _keysBack.Stop();
            _keysBack.Start();
        }

        private void GrabBusyKeys()
        {
            Dictionary<CapAction, HotkeySpec> grab = KeyGrab.Busy(_keys, _keyErrors, _settings.TakeBusyKeys);
            if (!_grabber.Set(grab)) return;
            foreach (CapAction a in grab.Keys) _keyErrors[a] = HotkeyOwners.TakenOver;
        }

        private void RetryBusyKeys()
        {
            if (_hotkeys.RetryFailed(_keys, _keyErrors)) CapStatus.Write(Elevation.IsElevated, _keyErrors, _startedUtc);
            UpdateRetry();
        }

        private void UpdateRetry()
        {
            bool any = false;
            foreach (int e in _keyErrors.Values) if (e > 0) any = true;
            if (any) _retry.Start(); else _retry.Stop();
        }

        public void Command(string command)
        {
            switch (command)
            {
                case "Shutdown":
                    // Идущая запись закрывается штатно (Finalize), иначе файл остался бы без длительности.
                    if (_record != null) { _record.StopNow(); _record = null; }
                    // Галерея ничего не держит несохранённым и остановке не мешает.
                    if (_gallery != null) { _gallery.Close(); _gallery = null; }
                    // Открытый редактор с несохранёнными правками спрашивает; «Отмена» оставляет процесс работать.
                    foreach (EditorForm f in new List<EditorForm>(_editors)) f.Close();
                    if (_editors.Count == 0) Application.ExitThread();
                    break;
                case "Reload": _settings = CapSettings.Load(); ApplySettings(); break;
                case "ShotRegion": Run(CapAction.ShotRegion); break;
                case "ShotScreen": Run(CapAction.ShotScreen); break;
                case "ShotWindow": Run(CapAction.ShotWindow); break;
                case "RecRegion": Run(CapAction.RecRegion); break;
                case "RecScreen": Run(CapAction.RecScreen); break;
                case "RecPause": Run(CapAction.RecPause); break;
                case "RecStop": if (_record != null) _record.RequestStop(); break;
                case "Open": OpenRequested(); break;
                case "Gallery": ShowGallery(null); break;
                case "Hud": HudLauncher.ToggleAsync(); break;
                case "HotkeysOff": HotkeysOff(); break;
                case "HotkeysOn": _settings = CapSettings.Load(); ApplySettings(); break;
            }
        }

        private void OnHotkey(CapAction action) { Run(action); }

        private void Run(CapAction action)
        {
            // Показатели не мешают ни выделению, ни записи — клавиша работает всегда.
            if (action == CapAction.Hud) { HudLauncher.ToggleAsync(); return; }
            if (action == CapAction.HudReset) { HudLauncher.CommandAsync("ResetStats", false); return; }
            if (action == CapAction.HudScene) { HudLauncher.CommandAsync("NextScene", true); return; }
            if (action == CapAction.LagRecord) { HudLauncher.CommandAsync("LagToggle", true); return; }
            if (action == CapAction.Borderless) { HudLauncher.CommandAsync("Borderless", true); return; }
            // Повторное нажатие «области» при открытом выделении закрывает его — нажатие не пропадает молча.
            if (_overlay != null)
            {
                if (action == CapAction.ShotRegion || action == CapAction.RecRegion) _overlay.Cancel();
                return;
            }
            // Клавиша записи во время записи — стоп (любая из двух: какой бы ни начали).
            if (_record != null)
            {
                if (action == CapAction.RecRegion || action == CapAction.RecScreen) { _record.RequestStop(); return; }
                if (action == CapAction.RecPause) { _record.TogglePause(); return; }
            }
            if (_delay != null) return;
            switch (action)
            {
                case CapAction.RecRegion: RecordRegion(); break;
                case CapAction.RecScreen: RecordScreen(); break;
                case CapAction.ShotRegion: WithDelay(ShotRegion); break;
                case CapAction.ShotScreen: WithDelay(ShotScreen); break;
                case CapAction.ShotWindow: WithDelay(ShotWindow); break;
                case CapAction.Gallery: ShowGallery(null); break;
            }
        }

        // Окно галереи одно на процесс: повторное нажатие поднимает уже открытое, а не плодит копии.
        // path — файл, который надо сразу выделить («Галерея» в уведомлении о только что сделанном снимке).
        private void ShowGallery(string path)
        {
            if (_gallery != null && _gallery.IsDisposed) _gallery = null;
            if (_gallery == null)
            {
                _settings = CapSettings.Load();
                GalleryForm form = new GalleryForm(_settings);
                form.EditRequested += EditFile;
                form.FormClosed += delegate { _gallery = null; };
                _gallery = form;
                form.Show();
            }
            else if (_gallery.WindowState == FormWindowState.Minimized) _gallery.WindowState = FormWindowState.Normal;
            _gallery.Activate();
            CapNative.SetForegroundWindow(_gallery.Handle);
            if (!string.IsNullOrEmpty(path)) _gallery.Reveal(path);
        }

        // Задержка нужна для снимков раскрытых меню и подсказок: нажали клавишу — успели открыть меню.
        private void WithDelay(Action shot)
        {
            int seconds = _settings.DelaySeconds;
            if (seconds <= 0) { shot(); return; }
            _delay = new System.Windows.Forms.Timer();
            _delay.Interval = seconds * 1000;
            _delay.Tick += delegate
            {
                _delay.Stop();
                _delay.Dispose();
                _delay = null;
                CapLog.Swallow(shot);
            };
            _delay.Start();
        }

        private void ShotScreen()
        {
            if (_settings.ScreenKey == ScreenTarget.ActiveWindow) { ShotWindow(); return; }
            List<MonitorInfo> monitors = ScreenGrab.Monitors();
            MonitorInfo mon = ScreenGrab.MonitorAt(monitors, CapNative.CursorPosition());
            Rectangle area = _settings.ScreenKey == ScreenTarget.AllMonitors || mon == null ? ScreenGrab.VirtualBounds(monitors) : mon.Bounds;
            string app = AppNaming.ForWindow(CapNative.GetForegroundWindow());
            Bitmap bmp;
            try { bmp = GrabClean(area); }
            catch (Exception ex) { Fail(ex, mon); return; }
            Deliver(bmp, app, mon, _settings.After);
        }

        private void ShotWindow()
        {
            IntPtr fg = CapNative.GetForegroundWindow();
            List<MonitorInfo> monitors = ScreenGrab.Monitors();
            Rectangle area = Rectangle.Empty;
            string cls = fg == IntPtr.Zero ? "" : CapNative.ClassOf(fg);
            if (fg != IntPtr.Zero && cls != "Progman" && cls != "WorkerW" && cls != "Shell_TrayWnd")
                area = Rectangle.Intersect(CapNative.VisualBounds(fg), ScreenGrab.VirtualBounds(monitors));
            MonitorInfo mon = area.IsEmpty ? ScreenGrab.MonitorAt(monitors, CapNative.CursorPosition()) : ScreenGrab.MonitorOf(monitors, area);
            if (area.Width <= 0 || area.Height <= 0) area = mon.Bounds;
            Bitmap bmp;
            try { bmp = GrabClean(area); }
            catch (Exception ex) { Fail(ex, mon); return; }
            Deliver(bmp, AppNaming.ForWindow(fg), mon, _settings.After);
        }

        private void ShotRegion()
        {
            List<MonitorInfo> monitors = ScreenGrab.Monitors();
            Rectangle all = ScreenGrab.VirtualBounds(monitors);
            Bitmap frame;
            // Мгновенно замораживается всё: меню и подсказки остаются на снимке, пока выделяешь.
            try { frame = GrabClean(all); }
            catch (Exception ex) { Fail(ex, null); return; }
            List<WindowCandidate> windows = WindowPicker.TopLevel(Process.GetCurrentProcess().Id);
            _overlay = new RegionOverlay(frame, all, monitors, windows, _settings.LastRegion);
            _overlay.EditStyle = EditorStyle.From(_settings);
            _overlay.Finished += OverlayFinished;
            _toasts.Hold();
            _overlay.ShowAndFocus();
        }

        // Видео области: то же выделение, что у снимка, но Enter начинает запись.
        private void RecordRegion()
        {
            List<MonitorInfo> monitors = ScreenGrab.Monitors();
            Rectangle all = ScreenGrab.VirtualBounds(monitors);
            Bitmap frame;
            try { frame = GrabClean(all); }
            catch (Exception ex) { Fail(ex, null); return; }
            List<WindowCandidate> windows = WindowPicker.TopLevel(Process.GetCurrentProcess().Id);
            _overlay = new RegionOverlay(frame, all, monitors, windows, _settings.LastRegion, true);
            _overlay.Finished += OverlayFinished;
            _toasts.Hold();
            _overlay.ShowAndFocus();
        }

        private void RecordScreen()
        {
            List<MonitorInfo> monitors = ScreenGrab.Monitors();
            MonitorInfo mon = ScreenGrab.MonitorAt(monitors, CapNative.CursorPosition());
            if (mon == null) return;
            RecordTarget t = new RecordTarget();
            t.Area = mon.Bounds;
            t.Monitor = mon;
            t.App = AppNaming.ForWindow(CapNative.GetForegroundWindow());
            StartRecording(t);
        }

        private void StartRecording(RecordTarget target)
        {
            if (_record != null) return;
            // Настройки могли поменяться на странице «Захват» без перезапуска агента.
            _settings = CapSettings.Load();
            RecordSession session = new RecordSession(_settings, target, Post);
            session.Completed += delegate(RecordSession s, ToastInfo toast)
            {
                if (_record == s) _record = null;
                if (toast != null) _toasts.Show(toast, target.Monitor);
            };
            _record = session;
            try { session.Begin(); }
            catch (Exception ex)
            {
                _record = null;
                session.Dispose();
                Fail(ex, target.Monitor);
            }
        }

        // Уведомления, не исключённые из захвата (Windows 10 до 2004), прячутся на кадр композиции и возвращаются.
        private Bitmap GrabClean(Rectangle area)
        {
            bool hid = _toasts.HideForCapture();
            try
            {
                if (hid) Thread.Sleep(60);
                return ScreenGrab.Grab(area, _settings.CursorInShots);
            }
            finally
            {
                if (hid) _toasts.RestoreAfterCapture();
            }
        }

        private void OverlayFinished(RegionOverlay overlay, OverlayResult result)
        {
            _overlay = null;
            _toasts.Release();
            try
            {
                if (result.Action == OverlayAction.Cancel || result.Area.Width <= 0 || result.Area.Height <= 0) return;
                _settings.LastRegion = result.Area;
                SaveLastRegion(result.Area);
                if (result.Action == OverlayAction.Record)
                {
                    RecordTarget t = new RecordTarget();
                    t.Window = result.Window;
                    t.Pid = result.Pid;
                    t.App = result.App;
                    t.Area = result.Window != IntPtr.Zero ? CapNative.VisualBounds(result.Window) : result.Area;
                    // Запись идёт в пределах одного монитора — того, где центр области.
                    t.Monitor = ScreenGrab.MonitorAt(overlay.Monitors, new Point(t.Area.X + t.Area.Width / 2, t.Area.Y + t.Area.Height / 2))
                                ?? ScreenGrab.MonitorOf(overlay.Monitors, result.Area);
                    if (t.Window == IntPtr.Zero && t.Monitor != null) t.Area = Rectangle.Intersect(t.Area, t.Monitor.Bounds);
                    StartRecording(t);
                    return;
                }
                MonitorInfo mon = ScreenGrab.MonitorOf(overlay.Monitors, result.Area);
                ShotAfter after = _settings.After;
                if (result.Action == OverlayAction.Copy) after = ShotAfter.CopyOnly;
                else if (result.Action == OverlayAction.Edit) after = ShotAfter.OpenEditor;
                else if (result.Action == OverlayAction.Save) after = after == ShotAfter.CopyOnly || after == ShotAfter.OpenEditor ? ShotAfter.Save : after;
                Bitmap bmp;
                if (result.Doc != null)
                {
                    // Нарисованное в оверлее: в редактор уходит сам документ (фигуры можно править дальше), иначе — готовая картинка.
                    SaveEditorStyle(overlay.EditStyle);
                    if (after == ShotAfter.OpenEditor && CapFeatures.Editor)
                    {
                        if (_settings.ShutterSound) Shutter.Play();
                        OpenEditor(result.Doc, result.App, null, mon);
                        return;
                    }
                    try { bmp = result.Doc.Render(null); }
                    finally { result.Doc.Dispose(); }
                }
                else bmp = ScreenGrab.Crop(overlay.Frame, overlay.Origin, result.Area);
                Deliver(bmp, result.App, mon, after);
            }
            finally
            {
                overlay.Dispose();
            }
        }

        // Последняя область пишется в файл настроек отдельно: остальные настройки агент не переписывает.
        private static void SaveLastRegion(Rectangle area)
        {
            CapSettings onDisk = CapSettings.Load();
            onDisk.LastRegion = area;
            onDisk.Save();
        }

        // Картинка уходит в файл и/или буфер обмена, затем — уведомление. Bitmap переходит во владение метода.
        private void Deliver(Bitmap bmp, string app, MonitorInfo mon, ShotAfter after)
        {
            if (_settings.ShutterSound) Shutter.Play();
            if (after == ShotAfter.OpenEditor && CapFeatures.Editor)
            {
                OpenEditor(bmp, app, null, mon);
                return;
            }
            Bitmap thumb = ToastHost.MakeThumbnail(bmp);
            if (after == ShotAfter.CopyOnly)
            {
                try { ImageStore.CopyImage(bmp); }
                catch (Exception ex) { thumb.Dispose(); bmp.Dispose(); Fail(ex, mon); return; }
                bmp.Dispose();
                _toasts.Show(ToastInfo.Copied(thumb), mon);
                return;
            }
            if (after == ShotAfter.SaveAndCopy)
            {
                try { ImageStore.CopyImage(bmp); }
                catch (Exception ex) { CapLog.Report(ex); }
            }
            string path;
            try
            {
                path = NameTemplate.BuildPath(_settings.EffectiveShotFolder, _settings.PerAppFolders, app,
                                              _settings.NameTemplate, DateTime.Now, _settings.ImageExtension, null);
            }
            catch (Exception ex) { thumb.Dispose(); bmp.Dispose(); Fail(ex, mon); return; }
            string format = _settings.ImageFormat;
            int quality = _settings.JpegQuality;
            bool copied = after == ShotAfter.SaveAndCopy;
            // Кодирование PNG 1440p — ~50 мс, 4K и три монитора — дольше: интерфейс агента не ждёт.
            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception error = null;
                try { ImageStore.Save(bmp, path, format, quality); }
                catch (Exception ex) { error = ex; }
                finally { bmp.Dispose(); }
                Post(delegate
                {
                    if (error != null) { thumb.Dispose(); Fail(error, mon); return; }
                    _toasts.Show(ToastInfo.Saved(thumb, path, copied), mon);
                });
            });
        }

        // Bitmap переходит во владение окна редактора. path — файл снимка, если он уже на диске.
        private void OpenEditor(Bitmap bmp, string app, string path, MonitorInfo mon)
        {
            EditorDoc doc;
            try { doc = new EditorDoc(bmp); }
            catch (Exception ex) { bmp.Dispose(); Fail(ex, mon); return; }
            OpenEditor(doc, app, path, mon);
        }

        private void OpenEditor(EditorDoc doc, string app, string path, MonitorInfo mon)
        {
            EditorForm editor;
            try { editor = new EditorForm(doc, app, path, mon, _settings); }
            catch (Exception ex) { doc.Dispose(); Fail(ex, mon); return; }
            _editors.Add(editor);
            editor.FormClosed += delegate
            {
                _editors.Remove(editor);
                SaveEditorStyle(editor.Style);
            };
            editor.Show();
            editor.Activate();
            CapNative.SetForegroundWindow(editor.Handle);
        }

        // Цвет, толщина и инструмент переживают закрытие редактора; остальные настройки агент не переписывает.
        private void SaveEditorStyle(EditorStyle style)
        {
            style.CopyTo(_settings);
            CapSettings onDisk = CapSettings.Load();
            style.CopyTo(onDisk);
            if (onDisk.EditorTool == EditTool.Crop) onDisk.EditorTool = EditTool.Arrow;
            onDisk.Save();
        }

        // Корневая папка, которой принадлежит файл: всех снимков или всех видео, а не подпапка программы, как
        // у «Показать в папке». Этим кнопка «Папка» в галерее отличается от «Показать в папке».
        internal static string GalleryFolder(CapSettings s, string savedPath)
        {
            return ImageStore.IsVideo(savedPath ?? "") ? s.EffectiveVideoFolder : s.EffectiveShotFolder;
        }

        // «Редактировать» в уведомлении: файл перечитывается с диска, не блокируя его.
        private void EditFile(string path, MonitorInfo mon)
        {
            Bitmap bmp;
            try { bmp = ImageStore.LoadUnlocked(path); }
            catch (Exception ex) { Fail(ex, mon); return; }
            string folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
            OpenEditor(bmp, folder, path, mon);
        }

        private void Fail(Exception ex, MonitorInfo mon)
        {
            CapLog.Report(ex);
            string text = ex is UnauthorizedAccessException
                ? Tr.S("нет доступа к папке", "no access to the folder")
                : ex.Message;
            _toasts.Show(ToastInfo.Error(Tr.S("Не удалось сохранить снимок", "Could not save the screenshot"), text), mon);
        }

        // Главное окно просит показать файл. Только изображение или видео, существующее на диске.
        private void OpenRequested()
        {
            string path;
            try { path = File.ReadAllText(CapPaths.OpenFile).Trim(); }
            catch { return; }
            if (path.Length == 0 || !File.Exists(path) || !(ImageStore.IsImage(path) || ImageStore.IsVideo(path))) return;
            ToastHost.OpenFile(path);
        }

        public void Dispose()
        {
            if (_overlay != null) { _overlay.Dispose(); _overlay = null; }
            if (_record != null) { _record.Dispose(); _record = null; }
            if (_gallery != null) { _gallery.Dispose(); _gallery = null; }
            if (_delay != null) { _delay.Dispose(); _delay = null; }
            foreach (EditorForm f in new List<EditorForm>(_editors)) f.ForceClose();
            _editors.Clear();
            _retry.Dispose();
            _buildWatch.Dispose();
            _grabber.Dispose();
            if (_keysBack != null) _keysBack.Dispose();
            _toasts.Dispose();
            _hotkeys.Dispose();
            _invoker.Dispose();
        }
    }
}
