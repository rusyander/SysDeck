# win-key-watch - detects and clears a phantom-latched modifier key, and logs
# every modifier transition with its injected flag so the culprit can be attributed.
#
# Watches all eight: LSHIFT RSHIFT LCTRL RCTRL LALT RALT LWIN RWIN.
# Observed latches so far: LWIN (05.09, 06.09), LSHIFT+LALT (09.09).
#
# Rule A: our hook saw the key go UP, but the system still reports it DOWN
#         -> the key-up was swallowed by a hook BEHIND us in the chain.
# Rule B: our hook never saw the key-up at all, yet the key has been "down"
#         far longer than any human hold while other typing happened.
#
# Both rules end in an injected KEYUP that unsticks the key. The log line says
# which rule fired, which is itself the diagnostic answer.

param(
    [string]$LogPath = "$PSScriptRoot\winkey.log",
    [int]$RuleAHoldMs = 1500,
    [int]$RuleBHoldMs = 60000,
    # lower this only to test the block path; 30s is the production value
    [int]$StormMinHoldMs = 30000
)

$ErrorActionPreference = 'Stop'

$cs = @'
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

public class WinKeyWatch
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const int WM_REHOOK = 0x0400 + 17;
    const uint LLKHF_INJECTED = 0x10, LLKHF_LOWER_IL_INJECTED = 0x02;
    const uint KEYEVENTF_KEYUP = 0x0002;
    // marker on our own synthetic key-ups so the hook does not count them as real
    const uint SELF_TAG = 0x57494E31;

    static readonly int[] VK = { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };
    static readonly string[] NAME = { "LSHIFT", "RSHIFT", "LCTRL", "RCTRL", "LALT", "RALT", "LWIN", "RWIN" };

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    static IntPtr _hook = IntPtr.Zero;
    static HookProc _proc;            // must stay referenced or the GC eats the callback
    static uint _mainThreadId;
    static string _logPath;
    static long _maxLogBytes = 2L * 1024 * 1024;
    static int _ruleAHoldMs, _ruleBHoldMs;

    static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
    static readonly object _fileLock = new object();

    // model of each modifier, built purely from events our hook actually saw
    static readonly bool[] _down = new bool[8];
    static readonly long[] _downAt = new long[8];
    static readonly long[] _upAt = new long[8];
    static long _otherKeyAt;
    static volatile bool _running = true;

    // a physically stuck key repeats at the typematic rate (~30/s) with no key-up.
    // no software key-up can win against that, so we block it in the hook instead.
    //
    // CAUTION - repeat rate alone does NOT separate stuck from held: a key the user is
    // simply HOLDING auto-repeats at exactly the same typematic rate. With the OS at
    // KeyboardSpeed=31 / KeyboardDelay=1 that is ~500ms of initial delay then ~32
    // repeats/s, so a 1-second hold of Ctrl reached the old 15-repeat threshold and the
    // key was blocked mid-hotkey (owner incident 09.09 21:50). The only real
    // discriminator is DURATION: a stuck key never stops. Both conditions must hold.
    static readonly int[] _repeat = new int[8];
    static readonly bool[] _suppress = new bool[8];
    static readonly long[] _lastEventAt = new long[8];
    static readonly long[] _lastNoteAt = new long[8];
    const int STORM_REPEATS = 15;      // consecutive downs with no up
    static int STORM_MIN_HOLD_MS;      // ...sustained this long = no human is holding it
    const int STORM_CLEAR_MS = 5000;   // silence this long = key is healthy again

    static int IndexOfVk(int vk)
    {
        for (int i = 0; i < VK.Length; i++) if (VK[i] == vk) return i;
        return -1;
    }

    static void Log(string line)
    {
        _queue.Enqueue("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + line);
    }

    static void Flush()
    {
        if (_queue.IsEmpty) return;
        lock (_fileLock)
        {
            try
            {
                var fi = new FileInfo(_logPath);
                if (fi.Exists && fi.Length > _maxLogBytes)
                {
                    string old = _logPath + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_logPath, old);
                }
                using (var w = new StreamWriter(_logPath, true))
                {
                    string line;
                    while (_queue.TryDequeue(out line)) w.WriteLine(line);
                }
            }
            catch { }
        }
    }

    static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var d = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                int msg = wParam.ToInt32();
                bool isDown = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
                bool isUp = (msg == WM_KEYUP || msg == WM_SYSKEYUP);
                bool mine = ((uint)d.dwExtraInfo.ToInt64() == SELF_TAG);
                int idx = IndexOfVk((int)d.vkCode);

                if (idx >= 0)
                {
                    int now = Environment.TickCount;
                    if (!mine && (isDown || isUp)) _lastEventAt[idx] = now;

                    bool repeat = isDown && _down[idx];
                    if (isDown && !mine)
                    {
                        if (repeat) _repeat[idx]++; else _repeat[idx] = 0;
                        long heldMs = (_downAt[idx] != 0) ? (now - _downAt[idx]) : 0;
                        if (_repeat[idx] >= STORM_REPEATS && heldMs > STORM_MIN_HOLD_MS && !_suppress[idx])
                        {
                            _suppress[idx] = true;
                            Log(string.Format("*** STUCK IN HARDWARE: {0} repeating at the typematic rate for {1}ms with"
                                + " no key-up -> blocking it at the hook. Physically fix or replace the key; stop this"
                                + " watcher to give {0} back.", NAME[idx], heldMs));
                        }
                    }
                    else if (isUp && !mine) _repeat[idx] = 0;

                    // log every event normally, but never the flood: one note per 10s while storming
                    if (!mine && (isDown || isUp) && (!repeat || (now - _lastNoteAt[idx]) > 10000))
                    {
                        if (repeat) _lastNoteAt[idx] = now;
                        bool injected = (d.flags & LLKHF_INJECTED) != 0;
                        bool lowIl = (d.flags & LLKHF_LOWER_IL_INJECTED) != 0;
                        Log(string.Format("{0,-6} {1} injected={2} lowIL={3} scan={4} extra=0x{5:X}{6}",
                            NAME[idx], isDown ? "DOWN" : "UP  ",
                            injected ? 1 : 0, lowIl ? 1 : 0, d.scanCode, d.dwExtraInfo.ToInt64(),
                            repeat ? string.Format("  [auto-repeat, {0} so far, held {1}ms{2}]", _repeat[idx],
                                (_downAt[idx] != 0 ? now - _downAt[idx] : 0),
                                (_suppress[idx] ? ", BLOCKED" : "")) : ""));
                    }

                    if (isDown) { if (!_down[idx]) _downAt[idx] = now; _down[idx] = true; }
                    else if (isUp) { _down[idx] = false; _upAt[idx] = now; }

                    // swallow the broken key's presses; let key-ups through so no one
                    // is left holding a stale "down" for it
                    if (_suppress[idx] && isDown && !mine) return (IntPtr)1;
                }
                else if (isDown)
                {
                    _otherKeyAt = Environment.TickCount;
                }
            }
            catch { }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    static void Release(int idx, string rule, string detail)
    {
        Log(string.Format("*** LATCH {0}: {1} stuck ({2}) -> injecting KEYUP", rule, NAME[idx], detail));
        keybd_event((byte)VK[idx], 0, KEYEVENTF_KEYUP, (UIntPtr)SELF_TAG);
        Thread.Sleep(60);
        bool still = (GetAsyncKeyState(VK[idx]) & 0x8000) != 0;
        Log(string.Format("    after release: {0} down={1}", NAME[idx], still));
        Flush();
    }

    static void CheckOne(int idx)
    {
        int now = Environment.TickCount;

        if (_suppress[idx])
        {
            // storm over -> the key is healthy (fixed, or the keyboard was replugged)
            if ((now - _lastEventAt[idx]) > STORM_CLEAR_MS)
            {
                _suppress[idx] = false;
                _repeat[idx] = 0;
                Log(string.Format("--- {0} quiet for {1}ms, un-blocking it", NAME[idx], STORM_CLEAR_MS));
            }
            // the flood may have left the system state down before we started blocking
            if ((GetAsyncKeyState(VK[idx]) & 0x8000) != 0)
                keybd_event((byte)VK[idx], 0, KEYEVENTF_KEYUP, (UIntPtr)SELF_TAG);
            return;
        }

        bool sysDown = (GetAsyncKeyState(VK[idx]) & 0x8000) != 0;
        if (!sysDown) return;

        // Rule A: we saw the release, the system did not. Unambiguous - fire fast.
        if (!_down[idx] && _upAt[idx] != 0 && (now - _upAt[idx]) > _ruleAHoldMs)
        {
            Release(idx, "RULE-A", "key-up seen by our hook but swallowed downstream, "
                + (now - _upAt[idx]) + "ms ago");
            return;
        }

        // Rule B: no release ever reached us. Only duration separates this from a
        // genuine hold, so the threshold is deliberately far beyond human holding.
        if (_down[idx] && _downAt[idx] != 0 && (now - _downAt[idx]) > _ruleBHoldMs
            && _otherKeyAt > _downAt[idx])
        {
            Release(idx, "RULE-B", "held " + (now - _downAt[idx]) + "ms with other keys typed; "
                + "key-up never reached our hook");
        }
    }

    static void Checker()
    {
        int tick = 0;
        while (_running)
        {
            try
            {
                for (int i = 0; i < VK.Length; i++) CheckOne(i);
                Flush();
                tick++;
                // stay at the head of the hook chain: apps that start later would
                // otherwise sit in front of us and rule A would go blind
                if (tick % 1200 == 0) PostThreadMessage(_mainThreadId, WM_REHOOK, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
            Thread.Sleep(500);
        }
    }

    static void Install()
    {
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new Exception("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
    }

    public static void Run(string logPath, int ruleAHoldMs, int ruleBHoldMs, int stormMinHoldMs)
    {
        _logPath = logPath;
        _ruleAHoldMs = ruleAHoldMs;
        _ruleBHoldMs = ruleBHoldMs;
        STORM_MIN_HOLD_MS = stormMinHoldMs;
        _mainThreadId = GetCurrentThreadId();

        Install();
        Log("=== win-key-watch started (pid " + System.Diagnostics.Process.GetCurrentProcess().Id
            + ", watching 8 modifiers, ruleA=" + ruleAHoldMs + "ms, ruleB=" + ruleBHoldMs
            + "ms, stormMinHold=" + stormMinHoldMs + "ms) ===");

        // a latch that predates us is invisible to both rules - our event model starts
        // empty - so sweep once at startup and clear whatever is already held
        for (int i = 0; i < VK.Length; i++)
        {
            if ((GetAsyncKeyState(VK[i]) & 0x8000) != 0)
            {
                Log("startup sweep: " + NAME[i] + " already down -> injecting KEYUP");
                keybd_event((byte)VK[i], 0, KEYEVENTF_KEYUP, (UIntPtr)SELF_TAG);
            }
        }
        Flush();

        var t = new Thread(Checker);
        t.IsBackground = true;
        t.Start();

        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_REHOOK)
            {
                UnhookWindowsHookEx(_hook);
                Install();
            }
        }

        _running = false;
        UnhookWindowsHookEx(_hook);
        Flush();
    }
}
'@

Add-Type -TypeDefinition $cs -Language CSharp | Out-Null

$dir = Split-Path -Parent $LogPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

[WinKeyWatch]::Run($LogPath, $RuleAHoldMs, $RuleBHoldMs, $StormMinHoldMs)
