// Windows Process Cleaner — снимок процессов, мониторинг CPU/простоя, кандидаты, завершение, память, TCP-порты
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WindowsProcessCleaner
{
    public partial class Engine
    {
        // ---------- Снимок процессов ----------
        private class RawProc { public int Pid; public int Ppid; public string Name; }

        private List<RawProc> Snapshot()
        {
            List<RawProc> list = new List<RawProc>();
            IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
            try
            {
                Native.PROCESSENTRY32 e = new Native.PROCESSENTRY32();
                e.dwSize = (uint)Marshal.SizeOf(typeof(Native.PROCESSENTRY32));
                if (Native.Process32First(snap, ref e))
                {
                    do
                    {
                        RawProc r = new RawProc();
                        r.Pid = (int)e.th32ProcessID;
                        r.Ppid = (int)e.th32ParentProcessID;
                        r.Name = e.szExeFile;
                        list.Add(r);
                    } while (Native.Process32Next(snap, ref e));
                }
            }
            finally { Native.CloseHandle(snap); }
            return list;
        }

        // visible  — процессы с видимым озаглавленным окном (для dev-режима);
        // anyTop   — процессы с любым верхнеуровневым окном, в т.ч. скрытым
        //            (для глобального режима: защищает свёрнутые в трей приложения).
        private void WindowPids(out HashSet<int> visible, out HashSet<int> anyTop)
        {
            HashSet<int> v = new HashSet<int>();
            HashSet<int> a = new HashSet<int>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                a.Add((int)pid);
                if (Native.IsWindowVisible(h) && Native.GetWindowTextLength(h) > 0)
                    v.Add((int)pid);
                return true;
            }, IntPtr.Zero);
            visible = v;
            anyTop = a;
        }

        // Границей каталога считаем разделитель: «C:\Program FilesX\…» не лежит
        // внутри «C:\Program Files», хотя строка с него начинается.
        private static bool UnderDir(string pathLower, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            string d = dir.ToLowerInvariant().TrimEnd('\\');
            if (d.Length == 0 || !pathLower.StartsWith(d)) return false;
            return pathLower.Length == d.Length || pathLower[d.Length] == '\\';
        }

        private bool IsUnderSystem(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.ToLowerInvariant();
            if (UnderDir(p, _winDir)) return true;
            if (p.Contains("\\windowsapps\\") || p.Contains("\\systemapps\\")) return true;
            return false;
        }

        private bool IsInstalledLocation(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.ToLowerInvariant();
            if (UnderDir(p, _programFiles)) return true;
            if (UnderDir(p, _programFilesX86)) return true;
            return false;
        }

        // Возвращает пары (pid, port) всех строк TCP; listeners — множество PID со LISTEN.
        public List<PortRow> TcpRows(out HashSet<int> listeners)
        {
            listeners = new HashSet<int>();
            List<PortRow> rows = new List<PortRow>();
            HashSet<long> seen = new HashSet<long>();
            ReadTcpTable(Native.AF_INET, rows, listeners, seen);
            ReadTcpTable(Native.AF_INET6, rows, listeners, seen);
            return rows;
        }

        // Одна таблица — IPv4 или IPv6. Node, Vite и .NET по умолчанию слушают «::» (только
        // v6): раньше такой порт в списке не появлялся, а процесс считался ничего не
        // слушающим и попадал в кандидаты на завершение.
        private static void ReadTcpTable(int af, List<PortRow> rows, HashSet<int> listeners, HashSet<long> seen)
        {
            int size = 0;
            Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, Native.TCP_TABLE_OWNER_PID_ALL, 0);
            if (size <= 0) return;
            size += 8192;                          // таблица могла вырасти между двумя вызовами
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                uint ret = Native.GetExtendedTcpTable(buf, ref size, false, af, Native.TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret != 0) return;
                int count = Marshal.ReadInt32(buf);
                IntPtr rowPtr = new IntPtr(buf.ToInt64() + 4);
                bool v6 = af == Native.AF_INET6;
                int rowSize = Marshal.SizeOf(v6 ? typeof(Native.MIB_TCP6ROW_OWNER_PID) : typeof(Native.MIB_TCPROW_OWNER_PID));
                for (int i = 0; i < count; i++)
                {
                    IntPtr at = new IntPtr(rowPtr.ToInt64() + i * rowSize);
                    uint state, localPort, pid;
                    if (v6)
                    {
                        Native.MIB_TCP6ROW_OWNER_PID r = (Native.MIB_TCP6ROW_OWNER_PID)Marshal.PtrToStructure(at, typeof(Native.MIB_TCP6ROW_OWNER_PID));
                        state = r.state; localPort = r.localPort; pid = r.owningPid;
                    }
                    else
                    {
                        Native.MIB_TCPROW_OWNER_PID r = (Native.MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(at, typeof(Native.MIB_TCPROW_OWNER_PID));
                        state = r.state; localPort = r.localPort; pid = r.owningPid;
                    }
                    if (state != Native.MIB_TCP_STATE_LISTEN) continue;
                    int port = ((int)(localPort & 0xFF) << 8) | (int)((localPort >> 8) & 0xFF);
                    listeners.Add((int)pid);
                    // один процесс обычно слушает и v4, и v6 на том же порту — строка одна
                    if (!seen.Add(((long)pid << 16) | (uint)port)) continue;
                    PortRow pr = new PortRow();
                    pr.Port = port;
                    pr.Pid = (int)pid;
                    rows.Add(pr);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ---------- Тик мониторинга: обновляет CPU% и время простоя ----------
        public void MonitorTick()
        {
            List<RawProc> snap = Snapshot();
            DateTime now = DateTime.Now;
            int cores = Environment.ProcessorCount;
            if (cores < 1) cores = 1;

            lock (_sync)
            {
                HashSet<int> alive = new HashSet<int>();
                double threshold = Config.CpuThresholdPercent;

                foreach (RawProc r in snap)
                {
                    alive.Add(r.Pid);
                    if (r.Pid <= 4) continue; // System Idle / System

                    ProcStat st = QueryStat(r.Pid, false, false);
                    if (!st.Ok) continue;

                    CpuSample prev;
                    if (_lastCpu.TryGetValue(r.Pid, out prev))
                    {
                        double wall = (now - prev.At).TotalMilliseconds;
                        if (wall > 0)
                        {
                            double pct = (st.Cpu - prev.Cpu).TotalMilliseconds / (wall * cores) * 100.0;
                            if (pct < 0) pct = 0;
                            _cpuPercent[r.Pid] = pct;
                            if (pct < threshold)
                            {
                                if (!_idleSince.ContainsKey(r.Pid)) _idleSince[r.Pid] = now;
                            }
                            else { _idleSince.Remove(r.Pid); }
                        }
                        prev.Cpu = st.Cpu; prev.At = now;
                    }
                    else
                    {
                        CpuSample cur = new CpuSample();
                        cur.Cpu = st.Cpu; cur.At = now;
                        _lastCpu[r.Pid] = cur;
                    }
                }

                ForgetDead(alive);
            }
        }

        private HashSet<string> WatchSet()
        {
            HashSet<string> s = new HashSet<string>();
            foreach (string w in Config.Watchlist) s.Add(w.Trim().ToLowerInvariant());
            return s;
        }

        private bool IsWhitelisted(string exe)
        {
            string n = exe.ToLowerInvariant();
            string noext = n.EndsWith(".exe") ? n.Substring(0, n.Length - 4) : n;
            foreach (string w in Config.Whitelist)
            {
                string ww = w.Trim().ToLowerInvariant();
                if (ww.Length == 0) continue;
                string wwNoext = ww.EndsWith(".exe") ? ww.Substring(0, ww.Length - 4) : ww;
                if (n == ww || noext == wwNoext) return true;
            }
            return false;
        }

        // ---------- Полное сканирование ----------
        // global=false — только процессы из watchlist (dev-режим).
        // global=true  — ВСЕ процессы; кандидаты отбираются с усиленными
        //                предохранителями (только свои процессы, не системные пути).
        public List<ProcInfo> Scan(bool global)
        {
            List<RawProc> snap = Snapshot();
            HashSet<string> watch = WatchSet();
            HashSet<int> visible, anyTop;
            WindowPids(out visible, out anyTop);
            HashSet<int> listeners;
            TcpRows(out listeners);

            HashSet<int> alive = new HashSet<int>(snap.Select(p => p.Pid));
            HashSet<int> parents = new HashSet<int>(snap.Select(p => p.Ppid));

            DateTime now = DateTime.Now;
            List<ProcInfo> result = new List<ProcInfo>();

            lock (_sync)
            foreach (RawProc r in snap)
            {
                bool inWatch = watch.Contains(r.Name.ToLowerInvariant());
                if (!global && !inWatch) continue;
                if (r.Pid <= 4 || r.Pid == _selfPid) continue;

                ProcInfo info = new ProcInfo();
                info.Pid = r.Pid;
                info.ParentPid = r.Ppid;
                info.Name = r.Name;
                info.Category = Categorize(r.Name);
                info.HasWindow = global ? anyTop.Contains(r.Pid) : visible.Contains(r.Pid);
                info.ListensTcp = listeners.Contains(r.Pid);
                info.HasChildren = parents.Contains(r.Pid);
                info.ParentAlive = r.Ppid != 0 && alive.Contains(r.Ppid);
                info.Whitelisted = IsWhitelisted(r.Name);
                info.UserOwned = true;

                double pct;
                info.CpuPercent = _cpuPercent.TryGetValue(r.Pid, out pct) ? pct : 0;
                DateTime since;
                info.IdleFor = _idleSince.TryGetValue(r.Pid, out since) ? (now - since) : TimeSpan.Zero;

                // Один OpenProcess даёт RAM, время старта, путь и (для глобального режима) SID.
                ProcStat st = QueryStat(r.Pid, true, global);
                info.RamBytes = st.WorkingSet;
                info.Path = st.Path ?? "";
                info.Uptime = st.Start > DateTime.MinValue ? now - st.Start : TimeSpan.Zero;
                if (info.Uptime < TimeSpan.Zero) info.Uptime = TimeSpan.Zero;

                if (global)
                {
                    info.IsSystemPath = IsUnderSystem(info.Path);
                    info.UserOwned = _currentUserSid != null && st.Sid != null && st.Sid == _currentUserSid;
                }

                EvaluateCandidate(info, global);
                result.Add(info);
            }
            return result;
        }

        private void EvaluateCandidate(ProcInfo p, bool global)
        {
            List<string> reasons = new List<string>();
            if (p.Whitelisted) { p.IsCandidate = false; p.Reason = Tr.S("в белом списке", "whitelisted"); return; }

            if (global)
            {
                if (IsCriticalName(p.Name)) { p.IsCandidate = false; p.Reason = Tr.S("защищённый процесс", "protected process"); return; }
                if (!p.UserOwned) { p.IsCandidate = false; p.Reason = Tr.S("не ваш процесс", "not your process"); return; }
                if (p.IsSystemPath) { p.IsCandidate = false; p.Reason = Tr.S("системный компонент", "system component"); return; }
                if (string.IsNullOrEmpty(p.Path)) { p.IsCandidate = false; p.Reason = Tr.S("нет доступа к пути", "no path access"); return; }
                if (Config.GlobalExcludeInstalled && IsInstalledLocation(p.Path))
                { p.IsCandidate = false; p.Reason = Tr.S("установленное приложение", "installed application"); return; }
            }

            if (p.Uptime.TotalMinutes < Config.MinLifetimeMinutes)
            { p.IsCandidate = false; p.Reason = Tr.S("молодой процесс", "too young"); return; }

            int idleReq = global ? Math.Max(Config.IdleMinutes, Config.GlobalIdleMinutes) : Config.IdleMinutes;
            bool parentDead = !p.ParentAlive;
            bool idleEnough = p.CpuPercent < Config.CpuThresholdPercent
                              && p.IdleFor.TotalMinutes >= idleReq;
            bool noWindow = !p.HasWindow;
            bool noTcp = !p.ListensTcp;
            bool noChildren = !p.HasChildren;

            if (parentDead && idleEnough && noWindow && noTcp && noChildren)
            {
                p.IsCandidate = true;
                p.Reason = Tr.S("родитель мёртв, простой, без окон/портов/детей",
                                "orphaned, idle, no windows/ports/children");
            }
            else
            {
                p.IsCandidate = false;
                if (p.ParentAlive) reasons.Add(Tr.S("жив родитель", "parent alive"));
                if (!idleEnough) reasons.Add(Tr.S("активен/мало простоя", "active/low idle"));
                if (p.HasWindow) reasons.Add(Tr.S("есть окно", "has window"));
                if (p.ListensTcp) reasons.Add(Tr.S("слушает порт", "listens on port"));
                if (p.HasChildren) reasons.Add(Tr.S("есть дочерние", "has children"));
                p.Reason = string.Join(", ", reasons.ToArray());
            }
        }

        // ---------- Завершение ----------
        // Возвращает true, если процесс завершён. freed — освобождённая RAM (WorkingSet до убийства).
        // Карта pid -> его верхнеуровневые окна. Строится ОДНИМ обходом:
        // отдельный EnumWindows на каждый убиваемый процесс — лишний обход всего рабочего стола.
        public Dictionary<int, List<IntPtr>> WindowsByPid()
        {
            Dictionary<int, List<IntPtr>> map = new Dictionary<int, List<IntPtr>>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint wp;
                Native.GetWindowThreadProcessId(h, out wp);
                int pid = (int)wp;
                List<IntPtr> lst;
                if (!map.TryGetValue(pid, out lst)) { lst = new List<IntPtr>(); map[pid] = lst; }
                lst.Add(h);
                return true;
            }, IntPtr.Zero);
            return map;
        }

        // true — такого PID в системе больше нет (в отличие от "нет прав открыть").
        private static bool PidGone(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero) { Native.CloseHandle(h); return false; }
            return Marshal.GetLastWin32Error() == 87; // ERROR_INVALID_PARAMETER
        }

        private const uint KillAccess = Native.PROCESS_QUERY_LIMITED_INFORMATION
                                      | Native.PROCESS_TERMINATE | Native.SYNCHRONIZE;

        public bool TerminateProcess(int pid, out long freed)
        {
            List<int> one = new List<int>(); one.Add(pid);
            return TerminateMany(one, out freed) > 0;
        }

        // Пакетное завершение. Ключевое отличие от «по одному»: WM_CLOSE рассылается
        // всем сразу и ожидание общее, поэтому 20 процессов стоят ~6 с, а не 20*6 с.
        public int TerminateMany(List<int> pids, out long freed)
        {
            return TerminateMany(pids, out freed, null, null);
        }

        // stage — что происходит прямо сейчас (для строки состояния), cancel опрашивается на
        // границах этапов и внутри пауз. Без них кнопки Dev Cleanup молчали все 3–20 секунд:
        // рассылка WM_CLOSE, общая пауза 2,5 с и добивание выглядели одним зависанием.
        // Отмена означает «не переходить к принудительному завершению»: уже закрывшиеся
        // процессы посчитаны, живые остаются жить.
        public int TerminateMany(List<int> pids, out long freed, Action<string> stage, Func<bool> cancel)
        {
            freed = 0;
            int killed = 0;
            if (pids == null || pids.Count == 0) return 0;

            List<int> unique = new List<int>(new HashSet<int>(pids));
            Dictionary<int, IntPtr> handles = new Dictionary<int, IntPtr>();
            Dictionary<int, long> ws = new Dictionary<int, long>();
            Dictionary<int, List<IntPtr>> winMap = WindowsByPid();

            try
            {
                int opened = 0;
                foreach (int pid in unique)
                {
                    if (cancel != null && cancel()) break;
                    opened++;
                    if (stage != null) stage(Tr.S("открываю процессы: ", "opening processes: ") + opened + "/" + unique.Count);
                    if (pid <= 4 || pid == _selfPid) continue;
                    IntPtr h = Native.OpenProcess(KillAccess | Native.PROCESS_VM_READ, false, pid);
                    if (h == IntPtr.Zero) h = Native.OpenProcess(KillAccess, false, pid);
                    if (h == IntPtr.Zero)
                    {
                        // уже умер — считаем задачу выполненной; иначе просто нет прав
                        if (PidGone(pid)) killed++;
                        continue;
                    }
                    handles[pid] = h;
                    ws[pid] = Native.QueryWorkingSet(h);
                }

                // 1) мягко: WM_CLOSE всем окнам всех процессов сразу
                bool anyWindow = false;
                if (stage != null) stage(Tr.S("прошу закрыться по-хорошему", "asking to close politely"));
                foreach (KeyValuePair<int, IntPtr> kv in handles)
                {
                    List<IntPtr> wins;
                    if (!winMap.TryGetValue(kv.Key, out wins)) continue;
                    foreach (IntPtr w in wins)
                    {
                        IntPtr res;
                        Native.SendMessageTimeout(w, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 200, out res);
                        anyWindow = true;
                    }
                }

                // 2) одно общее ожидание на всех
                if (anyWindow) SleepCancellable(2500, stage, cancel,
                                                Tr.S("жду закрытия окон", "waiting for the windows to close"));

                // 3) кто не ушёл — принудительно. Нажатый «Стоп» останавливает именно здесь:
                // добивать процессы после отмены — не то, чего ждёт нажавший.
                bool stop = cancel != null && cancel();
                List<int> forced = new List<int>();
                foreach (KeyValuePair<int, IntPtr> kv in handles)
                {
                    if (stop)
                    {
                        if (Native.WaitForSingleObject(kv.Value, 0) == Native.WAIT_OBJECT_0) { killed++; freed += ws[kv.Key]; }
                        continue;
                    }
                    if (Native.WaitForSingleObject(kv.Value, 0) == Native.WAIT_OBJECT_0)
                    {
                        killed++; freed += ws[kv.Key];
                        continue;
                    }
                    Native.TerminateProcess(kv.Value, 1);
                    forced.Add(kv.Key);
                }
                if (forced.Count > 0)
                {
                    if (stage != null) stage(Tr.S("завершаю принудительно: ", "terminating by force: ") + forced.Count);
                    SleepCancellable(500, null, null, null);
                    foreach (int pid in forced)
                    {
                        IntPtr h = handles[pid];
                        uint code;
                        bool gone = Native.WaitForSingleObject(h, 1500) == Native.WAIT_OBJECT_0
                                 || (Native.GetExitCodeProcess(h, out code) && code != Native.STILL_ACTIVE);
                        if (gone) { killed++; freed += ws[pid]; }
                    }
                }
            }
            finally
            {
                foreach (IntPtr h in handles.Values) Native.CloseHandle(h);
            }
            return killed;
        }

        // ---------- Очистка Standby Memory ----------
        public class MemResult { public bool Ok; public long FreedBytes; public string Message; }

        public MemResult PurgeStandby()
        {
            MemResult mr = new MemResult();
            Native.MEMORYSTATUSEX before = new Native.MEMORYSTATUSEX();
            before.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            Native.GlobalMemoryStatusEx(ref before);

            Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
            Native.EnablePrivilege("SeIncreaseQuotaPrivilege");

            // MemoryEmptyWorkingSets выдавливает страницы ВСЕХ процессов системы:
            // сразу после вызова всё, включая нас, тормозит, пока не загрузится обратно.
            // По умолчанию выключено — чистим только standby-список.
            if (Config.EmptyWorkingSets) SetMemoryList(Native.MemoryEmptyWorkingSets);
            int rc2 = SetMemoryList(Native.MemoryPurgeStandbyList);

            Native.MEMORYSTATUSEX after = new Native.MEMORYSTATUSEX();
            after.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            Native.GlobalMemoryStatusEx(ref after);

            long freed = (long)after.ullAvailPhys - (long)before.ullAvailPhys;
            mr.FreedBytes = freed > 0 ? freed : 0;

            if (rc2 == 0)
            {
                mr.Ok = true;
                mr.Message = Tr.S("Standby Memory очищена", "Standby Memory purged");
            }
            else if ((uint)rc2 == 0xC0000061)
            {
                mr.Ok = false;
                mr.Message = Tr.S("Нужны права администратора (перезапустите от админа)",
                                   "Administrator rights required (restart as admin)");
            }
            else
            {
                mr.Ok = false;
                mr.Message = Tr.S("NtSetSystemInformation вернул 0x", "NtSetSystemInformation returned 0x") + ((uint)rc2).ToString("X8");
            }
            return mr;
        }

        private int SetMemoryList(int command)
        {
            IntPtr p = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(p, command);
                return Native.NtSetSystemInformation(Native.SystemMemoryListInformation, p, sizeof(int));
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        // Пауза, которую можно прервать: 2,5 секунды сна на UI-потоке или без опроса отмены —
        // это то самое «нажал и ничего не происходит», ради которого всё и переписывалось.
        private static void SleepCancellable(int ms, Action<string> stage, Func<bool> cancel, string what)
        {
            int left = ms;
            while (left > 0)
            {
                if (cancel != null && cancel()) return;
                if (stage != null && what != null) stage(what + " (" + ((left + 999) / 1000) + Tr.S(" с)", " s)"));
                int slice = left > 250 ? 250 : left;
                Thread.Sleep(slice);
                left -= slice;
            }
        }

        // ---------- Массовое завершение по группе (Dev Cleanup) ----------
        public int TerminateByNames(string[] names, out long freed)
        {
            int matched;
            return TerminateByNames(names, out freed, out matched, null, null);
        }

        // matched — сколько процессов группы вообще нашлось. Без этого «завершено: 0» одинаково
        // означало и «нечего было завершать», и «не хватило прав»; на экране это выглядело
        // как кнопка, которая ничего не делает.
        public int TerminateByNames(string[] names, out long freed, out int matched,
                                    Action<string> stage, Func<bool> cancel)
        {
            List<int> pids = new List<int>();
            foreach (KeyValuePair<int, string> kv in MatchByNames(names)) pids.Add(kv.Key);
            matched = pids.Count;
            return TerminateMany(pids, out freed, stage, cancel);
        }

        // Кто попадёт под кнопку группы: процессы с этими именами минус белый список. Отдельно от
        // завершения, чтобы страница показала список ДО вопроса «завершить?» и завершила ровно его.
        public List<KeyValuePair<int, string>> MatchByNames(string[] names)
        {
            HashSet<string> want = new HashSet<string>(names.Select(n => n.ToLowerInvariant()));
            List<KeyValuePair<int, string>> found = new List<KeyValuePair<int, string>>();
            foreach (RawProc r in Snapshot())
                if (want.Contains(r.Name.ToLowerInvariant()) && !IsWhitelisted(r.Name))
                    found.Add(new KeyValuePair<int, string>(r.Pid, r.Name));
            return found;
        }

        // ---------- Занятые dev-порты ----------
        public List<PortRow> DevPortRows()
        {
            HashSet<int> listeners;
            List<PortRow> rows = TcpRows(out listeners);
            HashSet<int> devPorts = new HashSet<int>(Config.DevPorts);
            Dictionary<int, string> names = new Dictionary<int, string>();
            foreach (RawProc r in Snapshot()) names[r.Pid] = r.Name;

            List<PortRow> outRows = new List<PortRow>();
            HashSet<string> seen = new HashSet<string>();
            foreach (PortRow pr in rows)
            {
                if (!devPorts.Contains(pr.Port)) continue;
                string key = pr.Port + ":" + pr.Pid;
                if (seen.Contains(key)) continue;
                seen.Add(key);
                string nm;
                pr.ProcName = names.TryGetValue(pr.Pid, out nm) ? nm : "(pid " + pr.Pid + ")";
                outRows.Add(pr);
            }
            outRows.Sort(delegate(PortRow a, PortRow b) { return a.Port.CompareTo(b.Port); });
            return outRows;
        }
    }
}
